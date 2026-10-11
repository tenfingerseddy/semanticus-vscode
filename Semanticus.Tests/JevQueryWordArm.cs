// The word match for data questions: the comparison arm of the query.summarize replay (JevQueryReplay.cs), and
// nothing else. It is a best-effort reading with no service at all. Kane, 26 Sep: "Anything involving Jev is always
// Pro only", so no session is ever offered this reading; the replay scores it so Jev has a floor to beat.
//
// Plain string matching over the model's names, with the composer's typo rule (FuzzyWords). No inference.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Query;

namespace Semanticus.Tests
{
    internal static class JevQueryWordArm
    {
        private static readonly HashSet<string> Filler = new HashSet<string>(StringComparer.Ordinal)
        {
            "total", "sum", "of", "the", "a", "an", "amount", "value", "count", "number", "and", "per", "by", "in", "for",
        };

        private static readonly Regex Top = new Regex(@"\b(top|best|highest|biggest|largest|first)\s+(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Bottom = new Regex(@"\b(bottom|worst|lowest|smallest|last)\s+(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Split = new Regex(@"\b(?:by|per|for each|each|across)\s+(.+?)(?=$|[,;.]|\b(?:for|in|during|where|with|top|bottom|excluding|except|without|not|since|after|before|from|until|only)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly string[] Negations = { "not", "excluding", "except", "without", "exclude", "other than" };

        /// <summary>Words a name is made of: "NetSalesAmount" is net, sales, amount.</summary>
        internal static List<string> NameWords(string name) =>
            FuzzyWords.Words(Regex.Replace(name ?? "", @"(?<=[a-z])(?=[A-Z])", " ")).Select(w => w.Word).ToList();

        private static bool Has(List<(string Word, int At)> words, string w) => words.Any(q => q.Word == w || q.Word == w + "s" || FuzzyWords.Near(w, q.Word));

        public static JevQueryReader.Reading Read(string question, QueryCatalog cat, List<ValueCandidate> candidates)
        {
            var reading = new JevQueryReader.Reading();
            var words = FuzzyWords.Words(question).ToList();
            var used = new HashSet<int>();

            // Measures: every name word (fillers aside) is in the question. Failing that, the one best measure.
            var scored = cat.VisibleMeasures.Select(m =>
            {
                var nw = NameWords(m.Name).Where(w => !Filler.Contains(w)).ToList();
                if (nw.Count == 0) nw = NameWords(m.Name);
                var hit = nw.Count(w => Has(words, w));
                return (m, nw.Count, hit);
            }).ToList();
            var full = scored.Where(x => x.Count > 0 && x.hit == x.Count).OrderByDescending(x => x.Count).Select(x => x.m).ToList();
            if (full.Count > 0)
            {
                // A shorter name inside a longer chosen one is the longer one: "Sales" inside "Net Sales".
                foreach (var m in full)
                    if (!reading.Spec.Measures.Any(o => NameWords(o.Name).Intersect(NameWords(m.Name)).Count() == NameWords(m.Name).Count))
                        reading.Spec.Measures.Add(m);
            }
            else
            {
                var best = scored.Where(x => x.Count > 0 && x.hit * 2 >= x.Count && x.hit > 0).OrderByDescending(x => (double)x.hit / x.Count).ThenByDescending(x => x.hit).ToList();
                if (best.Count > 0 && (best.Count == 1 || (double)best[0].hit / best[0].Count > (double)best[1].hit / best[1].Count)) reading.Spec.Measures.Add(best[0].m);
            }
            foreach (var m in reading.Spec.Measures) foreach (var w in NameWords(m.Name)) MarkUsed(words, w, used);

            // Splits: the words after "by", "per" or "for each", cut at "and" or a comma.
            foreach (Match s in Split.Matches(question ?? ""))
                foreach (var part in Regex.Split(s.Groups[1].Value, @"\s*(?:,|\band\b|\bthen\b)\s*", RegexOptions.IgnoreCase))
                {
                    var pw = FuzzyWords.Words(part).Select(w => w.Word).ToList();
                    if (pw.Count == 0) continue;
                    var col = BestColumn(cat, pw);
                    if (col != null && !reading.Spec.GroupBy.Contains(col)) reading.Spec.GroupBy.Add(col);
                    foreach (var w in pw) MarkUsed(words, w, used);
                }

            // "Top 10 products": the things counted are a split too.
            int? topN = null;
            var lowest = false;
            var t = Top.Match(question ?? "");
            var b = Bottom.Match(question ?? "");
            var m2 = t.Success ? t : b.Success ? b : null;
            if (m2 != null)
            {
                topN = int.Parse(m2.Groups[2].Value, CultureInfo.InvariantCulture);
                lowest = !t.Success;
                var after = (question ?? "").Substring(m2.Index + m2.Length);
                var aw = FuzzyWords.Words(after).Select(w => w.Word).TakeWhile(w => w != "by" && w != "for" && w != "in").Take(2).ToList();
                if (aw.Count > 0)
                {
                    var col = BestColumn(cat, aw);
                    if (col != null && !reading.Spec.GroupBy.Contains(col)) reading.Spec.GroupBy.Add(col);
                    foreach (var w in aw) MarkUsed(words, w, used);
                }
            }

            // Filters: exact value matches on words nothing else used, "not" before them flips the filter.
            foreach (var c in candidates.Where(c => c.Exact && c.Kind == "value"))
            {
                if (Enumerable.Range(c.Start, c.Length).Any(used.Contains)) continue;
                if (reading.Filters.Any(f => f.Candidate.Start == c.Start)) continue;   // the first column that holds it
                var before = c.Start > 0 ? words[c.Start - 1].Word : "";
                var before2 = c.Start > 1 ? words[c.Start - 2].Word + " " + before : "";
                var op = Negations.Contains(before) || Negations.Contains(before2) ? QueryOps.IsNot : QueryOps.Is;
                if (c.Column.Kind == "whole")
                {
                    if (before == "after") op = QueryOps.After;
                    else if (before == "since" || before == "from") op = QueryOps.From;
                    else if (before == "before") op = QueryOps.Before;
                    else if (before == "until") op = QueryOps.UpTo;
                }
                reading.Filters.Add((c, op));
            }

            if (topN.HasValue && reading.Spec.Measures.Count > 0)
            {
                reading.Spec.Top = topN;
                reading.Spec.TopBy = reading.Spec.Measures[0];
                reading.Spec.Lowest = lowest;
            }
            if (reading.Spec.Measures.Count == 0) reading.Unsure.Add("No measure's name is in the words.");
            return reading;
        }

        /// <summary>The second baseline (fresh-bar.json): the word match with its splits replaced by the engine's own split
        /// shortlist, as many top picks as the word match found splits. The redesign's shortlist with no Jev.</summary>
        public static JevQueryReader.Reading ReadShortlist(string question, QueryCatalog cat, List<ValueCandidate> candidates)
        {
            var reading = Read(question, cat, candidates);
            var k = reading.Spec.GroupBy.Count;
            reading.Spec.GroupBy.Clear();
            if (k > 0) reading.Spec.GroupBy.AddRange(QuerySplitShortlist.For(question, cat, reading.Spec.Measures).Take(k));
            return reading;
        }

        private static void MarkUsed(List<(string Word, int At)> words, string w, HashSet<int> used)
        {
            for (var i = 0; i < words.Count; i++)
                if (words[i].Word == w || FuzzyWords.Near(w, words[i].Word) || words[i].Word == w + "s") used.Add(i);
        }

        private static QueryColumn BestColumn(QueryCatalog cat, List<string> words)
        {
            QueryColumn best = null;
            var bestScore = 0.0;
            foreach (var c in cat.Visible)
            {
                var nw = NameWords(c.Name);
                var tw = NameWords(c.Table);
                var hit = nw.Count(n => words.Any(w => w == n || FuzzyWords.Near(n, w) || w == n + "s"));
                if (hit == 0) continue;
                var tableHit = tw.Count(n => words.Any(w => w == n || FuzzyWords.Near(n, w) || w == n + "s"));
                var score = (double)hit / nw.Count + 0.5 * tableHit - 0.01 * (nw.Count - hit);
                if (score > bestScore) { bestScore = score; best = c; }
            }
            if (best != null) return best;
            // "products" names the Product table: take its name column, or the first text column a person can see.
            foreach (var c in cat.Visible.Where(c => c.Kind == "text"))
            {
                var tw = NameWords(c.Table);
                if (tw.Count == 0 || !tw.All(n => words.Any(w => w == n || w == n + "s" || FuzzyWords.Near(n, w)))) continue;
                var name = cat.Visible.FirstOrDefault(o => o.Table == c.Table && o.Kind == "text" && NameWords(o.Name).Contains("name"));
                return name ?? c;
            }
            return null;
        }
    }
}
