// The split shortlist: the few columns a data question most likely splits its numbers by, found on this computer.
//
// Why it exists (docs/redesign/jev-query-replay.md, runs 1 to 3): asked to pick the split from every column in the
// model, Jev cleared the bar on only 38% of splits, and 43 of 54 unsure readings failed on the split alone. So the
// engine narrows first and Jev answers a small choice: which of these few columns, or, when none fits, which table
// and then which column in it.
//
// The score is plain word matching (FuzzyWords, the composer's typo rule) against what a person sees: the column's
// name, its synonyms, its display folder and its table. Words after "by", "per", "each" or a top N count more,
// because that is where a split is said. Only columns that can split every chosen measure are listed (the reach
// rule the checks enforce later), never a key and never a hidden column. Nothing here reads a value.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Engine.Composer;

namespace Semanticus.Engine.Query
{
    internal static class QuerySplitShortlist
    {
        private static readonly HashSet<string> SplitWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "by", "per", "each", "across", "top", "bottom", "every", "which", "what", "breakdown", "split",
        };

        private static readonly HashSet<string> Filler = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "a", "an", "of", "and", "for", "in", "on", "to", "with", "is", "are", "was", "me", "show", "give",
            "how", "much", "many", "by", "per", "each", "top", "bottom", "all", "total", "only", "from", "at",
        };

        /// <summary>Words a name is made of: "NetSalesAmount" is net, sales, amount.</summary>
        internal static List<string> NameWords(string name) =>
            FuzzyWords.Words(Regex.Replace(name ?? "", @"(?<=[a-z])(?=[A-Z])", " ")).Select(w => w.Word).ToList();

        private static bool Same(string known, string typed) =>
            known == typed || typed == known + "s" || typed == known + "es" || known == typed + "s"
            || (known.EndsWith("y", StringComparison.Ordinal) && typed == known.Substring(0, known.Length - 1) + "ies")
            || FuzzyWords.Near(known, typed);

        /// <summary>The columns that can split every one of <paramref name="measures"/>, best first; only those the words
        /// touch at all. At most <paramref name="max"/>.</summary>
        public static List<QueryColumn> For(string words, QueryCatalog cat, IReadOnlyCollection<QueryMeasure> measures, int max = JevQueryReader.MaxShortlist) =>
            WithWhy(words, cat, measures, max).Select(x => x.Column).ToList();

        /// <summary>The same list, each with a plain line saying which of the question's own words matched what: a fact
        /// worked out here from names, safe to send.</summary>
        public static List<(QueryColumn Column, string Why)> WithWhy(string words, QueryCatalog cat, IReadOnlyCollection<QueryMeasure> measures, int max = JevQueryReader.MaxShortlist)
        {
            var asked = FuzzyWords.Words(words).Select(w => w.Word).ToList();
            // How much each asked word counts: more right after a split word, none for filler or a measure's own words.
            var measureWords = new HashSet<string>(measures.SelectMany(m => NameWords(m.Name)), StringComparer.Ordinal);
            var weight = new double[asked.Count];
            for (var i = 0; i < asked.Count; i++)
            {
                if (Filler.Contains(asked[i])) continue;
                weight[i] = measureWords.Any(m => Same(m, asked[i])) ? 0.25 : 1.0;
                for (var back = 1; back <= 3 && i - back >= 0; back++)
                    if (SplitWords.Contains(asked[i - back])) { weight[i] += 1.0; break; }
            }

            (double Score, HashSet<int> At) Hit(IEnumerable<string> nameWords)
            {
                var at = new HashSet<int>();
                var nw = nameWords.Where(w => !Filler.Contains(w)).ToList();
                if (nw.Count == 0) return (0, at);
                double total = 0;
                foreach (var n in nw)
                {
                    double best = 0;
                    var bestAt = -1;
                    for (var i = 0; i < asked.Count; i++)
                        if (weight[i] > best && Same(n, asked[i])) { best = weight[i]; bestAt = i; }
                    if (bestAt >= 0) at.Add(bestAt);
                    total += best;
                }
                return (total / nw.Count, at);
            }

            string Said(HashSet<int> at) => string.Join(" ", at.OrderBy(x => x).Select(i => asked[i]));
            var scored = new List<(QueryColumn C, double Score, int At, string Evidence, int Rank, string Why)>();
            var order = 0;
            foreach (var c in cat.Visible)
            {
                order++;
                if (c.Key) continue;
                // Only a column that narrows every table each measure counts: the same rule the reach check holds a
                // question to (LocalEngine.Query.cs Reach, QueryCatalog.Unreached), so the shortlist never offers a split
                // that would then be refused.
                if (measures.Count > 0 && !measures.All(m => cat.Unreached(c.Table, m).Length == 0)) continue;
                var name = Hit(NameWords(c.Name));
                var synonym = (c.Synonyms ?? Array.Empty<string>()).Select(x => Hit(NameWords(x))).DefaultIfEmpty((Score: 0.0, At: new HashSet<int>())).OrderByDescending(x => x.Item1).First();
                var folder = Hit(NameWords(c.Folder));
                var tableName = Hit(NameWords(c.Table));
                // A table answers to its synonyms as it does to its name ("by shop" is Store).
                var tableSynonym = (cat.TableSynonyms.TryGetValue(c.Table, out var ts) ? ts : Array.Empty<string>())
                    .Select(x => Hit(NameWords(x))).DefaultIfEmpty((Score: 0.0, At: new HashSet<int>())).OrderByDescending(x => x.Item1).First();
                var table = tableName.Score >= tableSynonym.Score ? tableName : tableSynonym;
                var own = name.Score >= synonym.Score ? name : synonym;
                // A table named in the words ("products") points at its text columns, its name column first.
                var nameColumn = table.Score > 0 && c.Kind == "text" && NameWords(c.Name).Contains("name") ? 0.3 : 0;
                var score = 3 * own.Score + 1.5 * folder.Score + table.Score + nameColumn;
                if (table.Score > 0 && own.Score == 0 && folder.Score == 0 && c.Kind != "text") score -= 0.5;
                if (score <= 0) continue;
                // The words this column answers to, not counting its table: columns of one table that answer to the same
                // words are twins ("by month": MonthName, MonthNumber, YearMonth), and only the one a person reads is kept.
                var evidence = string.Join(",", own.At.Union(folder.At).OrderBy(x => x));
                var rank = (c.SortHelper ? 0 : 4) + (c.Kind == "text" ? 2 : 0) + (NameWords(c.Name).Contains("name") ? 1 : 0);
                var why = own.At.Count > 0 ? $"the words '{Said(own.At)}' match its {(own == name ? "name" : "synonym")}"
                    : folder.At.Count > 0 ? $"the words '{Said(folder.At)}' match its folder"
                    : table == tableName ? $"the words '{Said(table.At)}' match its table"
                    : $"the words '{Said(table.At)}' match a synonym of its table";
                scored.Add((c, score, order, evidence, rank, why));
            }
            // A column named only through its table gives way to a column of that table the words name themselves.
            var namedTables = new HashSet<string>(scored.Where(x => x.Evidence.Length > 0).Select(x => x.C.Table), StringComparer.Ordinal);
            scored = scored.Where(x => x.Evidence.Length > 0 || !namedTables.Contains(x.C.Table))
                .GroupBy(x => (x.C.Table, x.Evidence))
                // The twin the words name most fully wins ("by year" is Year, not YearMonth); among equals, the one a
                // person reads (not a sort helper, text, a name column).
                .Select(g => g.OrderByDescending(x => Math.Round(x.Score, 6)).ThenByDescending(x => x.Rank).ThenBy(x => x.At).First())
                .ToList();
            return scored.OrderByDescending(x => x.Score).ThenBy(x => x.At).Take(max).Select(x => (x.C, x.Why)).ToList();
        }
    }
}
