// The query.summarize chooser: the parts of a data question, chosen by Jev from what the engine lists (Kane, 26 Sep).
//
// THE PATTERN: the engine lists and proves, Jev chooses, and Jev never writes text.
//   The engine lists: the measures and the columns a person can see (names, types, descriptions, folders), the
//   places the question's words matched a column's values (ValueMatcher, on this computer), and the numbers in the
//   words that could be a top N.
//   Jev answers typed questions only: which measure, how many splits, how each matched value is used, and whether
//   there is a top N. The answer reader refuses any id it did not offer.
//   THE SPLIT IS ASKED SECOND (redesign, Kane 26 Sep): picking the split from every column cleared the bar on only
//   38% of splits. Once the measures are settled the engine shortlists the few columns that reach them
//   (QuerySplitShortlist) and asks which one, each option saying which of the question's words matched it, with a way
//   out: which table, and then (a third call) which column in it.
//   The engine turns the picks into one fixed query (QueryTemplate), checks it, and runs it. Every word shown is the
//   engine's; the reading line is built from the parts.
//
// NEVER A SILENT GUESS, AND NEVER NO ANSWER (K16, Kane 8 Oct: "We should always return something"). A pick below the bar
// is Jev's best guess: it RUNS, and the answer says in one plain line what it assumed ("Using Net Sales. Metric Category is
// not filtered."), with each part one ask from being changed (Others). This is the Graphicus rule (Kane, 27 Sep and 3 Oct),
// ported back from Graphicus's copy of this file (src/Graphicus.Engine/Query/JevQueryReader.cs at 24e0bcf, same owner):
//   MAIN parts (the first number, the split the question surely names, the first narrowing the words name, the top few)
//   below the bar run as the best guess, said. OPTIONAL parts (a second number, a second split, any split when the count was
//   not sure, a later narrowing) below the bar are left out and offered, never run on a guess.
// A word that names the number asked for never narrows a column holding the same word as a value (NamesTheNumber). A sure
// "none of these" is a real answer (no second measure, no filter). The one stop left is a sure "this model does not hold
// that", which names the closest numbers instead.
//
// THE BAR. JevReads is TRUE: Kane decided on 28 September that Jev stays on for data questions, to be measured and
// tuned rather than gated ("we wont turn jev off for data questions, we will just investigate the issues and fine
// tune"; only the person can judge whether a reading matches what they meant). The replays are MEASUREMENTS now, not a
// gate. The latest, the third held-out set (fresh3-labels.json, 168, bar v5 in fresh3-bar.json, both committed before
// the run): Jev 119 right against the best baseline's 106, and 1 harmful mistake against 39; only Jev right on 40
// items and only the baseline right on 27, an exact McNemar p of 0.142, which bar v5 scored NOT LIVE.
// Read apart, Jev is behind on Northstar (67 of 100, baseline 77) and far ahead on contoso-ai (52 of 68, baseline 29).
// That is NOT raw against prepared: Northstar describes 4 of its 10 visible measures and gives one table synonyms, the
// two are different models, and 27 of contoso-ai's 68 questions use a synonym word that is in no object's name. Full
// numbers and those limits: docs/redesign/jev-query-replay.md.
//
// TUNED ON THE NATURAL SET (the r-jev-unsure investigation, built for real on 28 Sep, Kane). The bar is not lowered;
// the fixes are how we ask and how two good readings are read:
//   The first measure is asked for the FIRST number and the second for the SECOND: asked for "the number" when the
//   words ask for two, Jev put its weight on "none". With no number named, it picks the one that best answers.
//   The first measure has no "none": the fits question already says when nothing fits, and a lean to "does not fit"
//   below the bar is doubt, not thrown away.
//   A word the model names as another name for a measure is said in that measure's option (the model's own synonym,
//   and only when the person typed it; Kane's decision 2). The count question says a narrowing is not a split.
//   The same words matched in the same-named column of two tables are one question, their weight added.
//   TWO GOOD READINGS (Kane's decision 1): when Jev's top two picks are both real and of the same kind (a measure and
//   a measure; a column and a column of the same table and type, or a twin) and together clear the bar, the top one is
//   taken and the other is kept as an Other: the answer says "Showing Net Sales. You may have meant Units." and the
//   other reading's parts ride with it, one ask away. It is never "none" and never a different kind of thing.
//
// NEVER A NUMBER THE QUESTION DID NOT ASK FOR, UNSAID (Kane's ruling, 28 Sep, round 2):
//   Two values of one column the words name ("coast vs north", "did 2025 beat 2024", "coast and north") are shown side
//   by side, split by that column, never added into one total; with no room for the split, nothing runs. Words in a
//   column that two tables hold (Store Region and Customer Region) are read on ONE table for every word, the one Jev
//   leaned to over them all, and the other table is named with its own parts. A comparison no longer takes a split
//   away from the count Jev gave; the split question keeps a way out instead.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Decisions;
using Semanticus.Engine.Composer;

namespace Semanticus.Engine.Query
{
    internal static class JevQueryReader
    {
        /// <summary>WHETHER THIS BUILD LETS JEV READ DATA QUESTIONS AT ALL. True: Kane kept Jev on for data questions
        /// (28 Sep), whatever a replay scores; the replays measure it (docs/redesign/jev-query-replay.md).
        /// Per model, the query.summarize switch then says whether it does. Free never asks: ask_data is Pro only.</summary>
        internal const bool JevReads = true;

        /// <summary>The probability Jev gave one option. A reply with no probabilities gives the pick its confidence.</summary>
        private static double Prob(DecisionAnswer a, string id) =>
            a == null || id == null ? 0 : a.Probabilities.TryGetValue(id, out var p) ? p : (a.OptionId == id ? a.Confidence : 0);

        /// <summary>The model's other names for a measure that the question's words say in full. Only these are ever
        /// named to Jev, so a synonym the person did not type never leaves.</summary>
        internal static List<string> SaidSynonyms(string words, string[] synonyms)
        {
            var asked = FuzzyWords.Words(words ?? "").Select(w => w.Word).ToList();
            return (synonyms ?? Array.Empty<string>()).Where(syn =>
            {
                var sw = FuzzyWords.Words(syn).Select(w => w.Word).ToList();
                return sw.Count > 0 && sw.All(w => asked.Any(a => a == w || FuzzyWords.Near(w, a)));
            }).ToList();
        }

        private static readonly Regex CompareWords = new Regex(@"\b(vs\.?|versus|compared\s+(to|with)|against|beat|beats)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>"coast vs north": two values of one column, compared. Both are kept and shown side by side, split by
        /// that column, so neither is asked about as a filter. A pair whose column has a twin in another table is left to
        /// the twin question, which says which table.</summary>
        internal static List<List<ValueCandidate>> CompareGroups(string words, IEnumerable<ValueCandidate> cands)
        {
            if (!CompareWords.IsMatch(words ?? "")) return new List<List<ValueCandidate>>();
            var groups = cands.Where(c => c.Kind == "value").GroupBy(c => c.Column)
                .Where(g => g.Select(c => c.Words.ToLowerInvariant()).Distinct().Count() >= 2).Select(g => g.ToList()).ToList();
            return groups.Where(g => !groups.Any(o => o != g && string.Equals(o[0].Column.Name, g[0].Column.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>Twin filter candidates: the same words matched the same-named column in more than one table (Store
        /// Region and Customer Region both hold "South"). A word already kept by a comparison on one table is not asked
        /// again, so it never lands on the other table beside it.</summary>
        internal static List<List<ValueCandidate>> TwinGroups(IEnumerable<ValueCandidate> cands, string words = null)
        {
            var list = cands.ToList();
            var compared = new HashSet<ValueCandidate>(words == null ? Enumerable.Empty<ValueCandidate>() : CompareGroups(words, list).SelectMany(g => g));
            return list.GroupBy(c => (c.Words.ToLowerInvariant(), c.Column.Name.ToLowerInvariant(), c.Kind))
                .Where(g => g.Select(c => c.Column.Table).Distinct().Count() > 1 && !g.Any(compared.Contains)).Select(g => g.ToList()).ToList();
        }

        /// <summary>Twin groups whose column is named by two or more of the words (coast and north, each in Store Region and
        /// Customer Region): read together, on one table, in the order the words come.</summary>
        internal static List<List<List<ValueCandidate>>> TwinFamilies(List<List<ValueCandidate>> twins) =>
            twins.GroupBy(g => (g[0].Column.Name.ToLowerInvariant(), g[0].Kind)).Where(f => f.Count() >= 2)
                .Select(f => f.OrderBy(g => g[0].Start).ToList()).ToList();

        /// <summary>A candidate whose words a comparison already keeps on the same-named column of another table ('coast'
        /// in Customer Region, beside "south vs coast" kept on Store Region, the one table holding both): not asked or read
        /// on its own, so the same word never narrows both tables at once.</summary>
        internal static bool Covered(ValueCandidate c, IEnumerable<ValueCandidate> compared) =>
            compared.Any(k => k != c && k.Column.Table != c.Column.Table && k.Kind == c.Kind
                && string.Equals(k.Column.Name, c.Column.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(k.Words, c.Words, StringComparison.OrdinalIgnoreCase));

        /// <summary>Candidates in one table only whose column shares its name with twin columns the other words matched
        /// ('south' is only in Store Region; 'coast' is in Store Region and Customer Region). Asked the plain way, and read
        /// with the twins as one family, so "south and coast" is never South stores beside Coast customers.</summary>
        internal static List<List<ValueCandidate>> Partners(IEnumerable<ValueCandidate> cands, List<List<ValueCandidate>> twins, IEnumerable<ValueCandidate> compared)
        {
            var inTwins = new HashSet<ValueCandidate>(twins.SelectMany(g => g));
            var done = compared.ToList();
            return cands.Where(c => c.Kind == "value" && !inTwins.Contains(c) && !done.Contains(c) && !Covered(c, done)
                    && twins.Any(g => g[0].Kind == c.Kind && string.Equals(g[0].Column.Name, c.Column.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(c => new List<ValueCandidate> { c }).ToList();
        }

        /// <summary>Two or more of the words match values of one column (or of same-named columns): a comparison may be
        /// the split, so the split question keeps a way out.</summary>
        private static bool MayCompare(IEnumerable<ValueCandidate> cands) =>
            cands.Where(c => c.Kind == "value").GroupBy(c => c.Column.Name.ToLowerInvariant())
                .Any(g => g.Select(c => c.Words.ToLowerInvariant()).Distinct().Count() >= 2);

        public const string Prefix = "q:";
        public const string None = "none";

        /// <summary>The most questions one call asks. tools/relay/templates.mjs holds the relay's bound to it.</summary>
        public const int MaxQuestions = 24;

        public const int GroupSlots = 2;
        public const int MeasureSlots = 2;

        /// <summary>The most measures and columns listed in one call. A bigger model lists the ones whose names share
        /// the most words with the question first.</summary>
        public const int MaxListed = 150;

        /// <summary>The longest description sent, in characters.</summary>
        public const int MaxDescription = 200;

        public const string Fits = Prefix + "fits";
        /// <summary>The check, asked once the reading is settled and before it runs: what is missing, and whether the reading
        /// answers the question (JevQueryChecker).</summary>
        public const string CheckGapQ = Prefix + "check:gap";
        /// <summary>The second opinion on one gap the first check named: does the question really ask for it?</summary>
        public const string CheckRealQ = Prefix + "check:real";
        public static string MeasureQ(int slot) => Prefix + "measure:" + slot.ToString(CultureInfo.InvariantCulture);
        public const string SplitsQ = Prefix + "splits";
        public static string GroupQ(int slot) => Prefix + "group:" + slot.ToString(CultureInfo.InvariantCulture);
        public static string GroupTableQ(int slot) => Prefix + "group_table:" + slot.ToString(CultureInfo.InvariantCulture);
        public static string GroupInQ(int slot) => Prefix + "group_in:" + slot.ToString(CultureInfo.InvariantCulture);

        /// <summary>The most columns a split shortlist offers.</summary>
        public const int MaxShortlist = 6;

        /// <summary>The way out of a shortlist: the split is another column.</summary>
        public const string Other = "other";

        /// <summary>The line a split gets when the model shows no column to split by at all, so nothing can be offered.</summary>
        public const string NothingToSplitBy = "This model shows no column to split the numbers by.";
        public static string FilterQ(string id) => Prefix + "filter:" + id;
        public const string TopQ = Prefix + "top";
        public const string TopByQ = Prefix + "top_by";
        public const string TopEndQ = Prefix + "top_end";
        public const string SortQ = Prefix + "sort";

        /// <summary>How sure the order pick must be to be taken (below the decision's own bar): more likely than not.</summary>
        internal const double SortBar = 0.5;

        public static string MeasureId(QueryMeasure m) => "m:" + m.Name;
        public static string ColumnId(QueryColumn c) => "c:" + c.Id;
        public static string TableId(string table) => "t:" + table;

        internal sealed class Request
        {
            public string Words;
            public List<QueryMeasure> Measures = new List<QueryMeasure>();
            public List<QueryColumn> Columns = new List<QueryColumn>();
            public List<ValueCandidate> Candidates = new List<ValueCandidate>();
            /// <summary>The tables offered for a split, in the second call.</summary>
            public List<string> Tables = new List<string>();
            public List<int> Numbers = new List<int>();
            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;
        }

        private static readonly Regex Number = new Regex(@"(?<![\p{L}\p{N}.])(\d{1,4})(?![\p{L}\p{N}.])", RegexOptions.CultureInvariant);

        /// <summary>The numbers in the words that could be how many rows to keep: 1 to 1000.</summary>
        public static List<int> NumbersIn(string words) =>
            Number.Matches(words ?? "").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Where(n => n >= 1 && n <= 1000).Distinct().ToList();

        /// <summary>How many of the question's own words a name, or any of its synonyms, shares: "revenue" names Net
        /// Sales when that is one of its synonyms. The first call lists by this, and the third call cuts its offer by it.</summary>
        private static Func<string, string[], int> Namer(string words)
        {
            var asked = FuzzyWords.Words(words ?? "").Select(w => w.Word).ToList();
            int Overlap(string name) => FuzzyWords.Words(name).Count(w => asked.Any(a => a == w.Word || FuzzyWords.Near(w.Word, a)));
            return (name, synonyms) => Math.Max(Overlap(name), (synonyms ?? Array.Empty<string>()).Select(Overlap).DefaultIfEmpty(0).Max());
        }

        public static Request Build(string words, QueryCatalog cat, List<ValueCandidate> candidates)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.QuerySummarize);
            var r = new Request { Words = (words ?? "").Trim() };
            var Named = Namer(r.Words);
            string[] TableTerms(string table) => cat.TableSynonyms.TryGetValue(table, out var t) ? t : null;

            r.Candidates = candidates.Take(ValueMatcher.MaxCandidates).ToList();
            // A column the words matched a value in is asked about as a filter, so it is always listed, described.
            var matched = new HashSet<QueryColumn>(r.Candidates.Select(c => c.Column));
            r.Measures = cat.VisibleMeasures.Select((m, i) => (m, i)).OrderByDescending(x => Named(x.m.Name, x.m.Synonyms)).ThenBy(x => x.i)
                .Take(MaxListed).OrderBy(x => x.i).Select(x => x.m).ToList();
            r.Columns = cat.Visible.Select((c, i) => (c, i)).OrderByDescending(x => matched.Contains(x.c))
                .ThenByDescending(x => Named(x.c.Name, x.c.Synonyms) + Named(x.c.Table, TableTerms(x.c.Table))).ThenBy(x => x.i)
                .Take(MaxListed).OrderBy(x => x.i).Select(x => x.c).ToList();
            r.Numbers = NumbersIn(r.Words).Take(4).ToList();

            var q = new List<DecisionQuestion>
            {
                new DecisionQuestion(Fits, DecisionQuestionKind.Noul,
                    "Can the question in question_words be answered with the measures and columns listed in model_fields?",
                    new[]
                    {
                        new DecisionOption("yes", "the question asks for numbers that the listed measures give, split or narrowed by the listed columns"),
                        new DecisionOption("no", "the question asks for something the listed measures and columns do not hold"),
                    }),
            };

            // The FIRST and the SECOND number: asked for "the number" when the words ask for two, Jev put its weight on
            // "none" (0.23 to 0.59), not on the second measure. The first has no "none": the fits question says when
            // nothing fits, and a question that names no number ("how are we doing") is told what to pick.
            q.Add(new DecisionQuestion(MeasureQ(1), DecisionQuestionKind.Choice,
                "Which measure gives the FIRST number the question asks for (the only one, if it asks for one)? If the question names no number at all (how are we doing, how is it going, which sells most), the measure that best answers it.",
                r.Measures.Select(m => new DecisionOption(MeasureId(m), MeasureOption(r.Words, m)))
                    // A model showing one measure keeps the way out, so the question still has two answers to pick from.
                    .Concat(r.Measures.Count < 2 ? new[] { new DecisionOption(None, "no listed measure gives the number the question asks for") } : Array.Empty<DecisionOption>())));
            for (var slot = 2; slot <= MeasureSlots; slot++)
                q.Add(new DecisionQuestion(MeasureQ(slot), DecisionQuestionKind.Choice,
                    "The question may ask for more than one number. Which measure gives the SECOND number it asks for, the one it names after the first?",
                    r.Measures.Select(m => new DecisionOption(MeasureId(m), MeasureOption(r.Words, m)))
                        .Append(new DecisionOption(None, "the question asks for no second number"))));

            q.Add(new DecisionQuestion(SplitsQ, DecisionQuestionKind.Choice,
                "How many different things does the question split its numbers by (the words after 'by', 'per' or 'for each', or the things a top N counts)? Words that only narrow the numbers to one named thing (one product, one place, one year) are not a split.",
                new[]
                {
                    new DecisionOption("0", "none: the question asks for one total, even when it narrows that total to one named thing"),
                    new DecisionOption("1", "one thing, such as by region, or the top 5 products"),
                    new DecisionOption("2", "two things, such as by region and by year"),
                }));

            // The same words in the same-named column of two tables: one question, so their weight is not split between
            // two questions that each look unsure (and could each say yes, running both filters together).
            var twins = TwinGroups(r.Candidates, r.Words);
            foreach (var g in twins)
            {
                var ops = g[0].Kind == "year" ? QueryOps.All.Where(QueryOps.Compares) : g[0].Column.Kind == "text" ? QueryOps.ForText : QueryOps.All;
                var names = string.Join(" and ", g.Select(c => c.Column.Table + " " + c.Column.Name));
                q.Add(new DecisionQuestion(FilterQ(g[0].Id), DecisionQuestionKind.Choice,
                    $"The words '{g[0].Words}' in the question match a value in {names}. Either column can be meant. How does the question use them?",
                    g.SelectMany(c => ops.Select(op => new DecisionOption(op + "|" + c.Id, OpCriterion(op, c.Column.Table + " " + c.Column.Name, c.Words))))
                        .Append(new DecisionOption(None, $"the words '{g[0].Words}' do not narrow {names} here: they mean something else"))));
            }
            var comparedInBuild = CompareGroups(r.Words, r.Candidates).SelectMany(g => g).ToList();
            var asked = new HashSet<ValueCandidate>(twins.SelectMany(g => g).Concat(comparedInBuild));
            foreach (var c in r.Candidates.Where(c => !asked.Contains(c) && !Covered(c, comparedInBuild)))
            {
                var col = c.Column.Table + " " + c.Column.Name;
                var ops = c.Kind == "year" ? QueryOps.All.Where(QueryOps.Compares)
                    : c.Column.Kind == "text" ? QueryOps.ForText : QueryOps.All;
                q.Add(new DecisionQuestion(FilterQ(c.Id), DecisionQuestionKind.Choice,
                    c.Kind == "year"
                        ? $"Column {col} holds years, and the words '{c.Words}' in the question are a year. How does the question use them?"
                        : $"The words '{c.Words}' in the question match a value in column {col}. How does the question use them?",
                    ops.Select(op => new DecisionOption(op, OpCriterion(op, col, c.Words)))
                        .Append(new DecisionOption(None, $"the words '{c.Words}' do not narrow {col} here: they mean something else, or another column is meant"))));
            }

            if (r.Numbers.Count > 0)
            {
                q.Add(new DecisionQuestion(TopQ, DecisionQuestionKind.Choice,
                    "Does the question ask for only the top or bottom few rows, and how many?",
                    r.Numbers.Select(n => new DecisionOption("n:" + n.ToString(CultureInfo.InvariantCulture),
                            "the question asks for the top or bottom " + n.ToString(CultureInfo.InvariantCulture)))
                        .Append(new DecisionOption(None, "the question asks for every row, not a top or bottom few"))));
                q.Add(new DecisionQuestion(TopByQ, DecisionQuestionKind.Choice,
                    "If the question asks for a top or bottom few, which measure ranks them?",
                    r.Measures.Select(m => new DecisionOption(MeasureId(m), "the rows are ranked by " + m.Name))
                        .Append(new DecisionOption(None, "the question asks for no top or bottom few"))));
                q.Add(new DecisionQuestion(TopEndQ, DecisionQuestionKind.Choice,
                    "If the question asks for a top or bottom few, which end?",
                    new[]
                    {
                        new DecisionOption("highest", "the question wants the largest values: top, best, most"),
                        new DecisionOption("lowest", "the question wants the smallest values: bottom, worst, least"),
                    }));
            }

            q.Add(new DecisionQuestion(SortQ, DecisionQuestionKind.Choice,
                "How does the question want the rows ordered?",
                new[]
                {
                    new DecisionOption("measure_high", "largest number first"),
                    new DecisionOption("measure_low", "smallest number first"),
                    new DecisionOption("group_a_z", "in the order of the thing they are split by, A to Z or earliest first"),
                    new DecisionOption("group_z_a", "in the order of the thing they are split by, Z to A or latest first"),
                    new DecisionOption(None, "the question says nothing about the order"),
                }));

            r.Questions = q.Take(MaxQuestions).ToList();
            r.Snapshot = Snapshot(point, r, withDescriptions: true);
            try { DecisionSnapshot.RequireWithin(r.Snapshot, 60 * 1024); }
            catch (InvalidOperationException) { r.Snapshot = Snapshot(point, r, withDescriptions: false); }
            return r;
        }

        /// <summary>A measure's option. When the question uses one of the model's own other names for it, the option says
        /// so (Kane, 28 Sep): the synonyms are matched here, and the word named is the person's own, never another.</summary>
        private static string MeasureOption(string words, QueryMeasure m)
        {
            var said = SaidSynonyms(words, m.Synonyms);
            return "the question asks for " + m.Name + (said.Count == 0 ? ""
                : " (the model says the question's word '" + said.OrderByDescending(x => x.Length).First() + "' is another name for " + m.Name + ")");
        }

        private static string OpCriterion(string op, string col, string words) => op switch
        {
            QueryOps.Is => $"the question keeps only rows where {col} is '{words}'",
            QueryOps.IsNot => $"the question leaves out rows where {col} is '{words}'",
            QueryOps.After => $"the question keeps rows where {col} comes after '{words}'",
            QueryOps.From => $"the question keeps rows where {col} is '{words}' or later",
            QueryOps.Before => $"the question keeps rows where {col} comes before '{words}'",
            QueryOps.UpTo => $"the question keeps rows where {col} is '{words}' or earlier",
            _ => op,
        };

        private static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? null : (s.Trim().Length <= MaxDescription ? s.Trim() : s.Trim().Substring(0, MaxDescription).TrimEnd() + "...");

        /// <summary>What a measure sends: its name, table, folder and description. Never its formula.</summary>
        internal static object MeasureField(QueryMeasure m, bool withDescriptions)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal) { { "name", m.Name }, { "table", m.Table } };
            if (!string.IsNullOrWhiteSpace(m.Folder)) d["folder"] = m.Folder;
            if (withDescriptions && Trim(m.Description) != null) d["description"] = Trim(m.Description);
            return d;
        }

        /// <summary>What a column sends: its table, name, type, folder and description. Never a value.</summary>
        internal static object ColumnField(QueryColumn c, bool withDescriptions)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal) { { "table", c.Table }, { "name", c.Name }, { "type", c.Kind } };
            if (!string.IsNullOrWhiteSpace(c.Folder)) d["folder"] = c.Folder;
            if (withDescriptions && Trim(c.Description) != null) d["description"] = Trim(c.Description);
            return d;
        }

        private static IReadOnlyDictionary<string, object> Snapshot(DecisionPoint point, Request r, bool withDescriptions)
        {
            var measures = r.Measures.Select(m => MeasureField(m, withDescriptions)).ToList();
            var columns = r.Columns.Select(c => ColumnField(c, withDescriptions)).ToList();
            var matches = r.Candidates.Select(c => (object)new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "id", c.Id }, { "column", c.Column.Table + "[" + c.Column.Name + "]" }, { "words", c.Words },
                { "kind", c.Kind == "year" ? "the column holds years and the words are a year" : "a value in the column matches the words" },
            }).ToList();
            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("question_words", r.Words),
                new KeyValuePair<string, object>("model_fields", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "measures", measures }, { "columns", columns },
                }),
                new KeyValuePair<string, object>("value_matches", matches),
            });
        }

        /// <summary>What the answer came to: the parts Jev was sure of, and a plain line for each it was not.</summary>
        internal sealed class Reading
        {
            public QuerySpec Spec = new QuerySpec();
            /// <summary>The filters Jev chose, by candidate, so the engine keeps the values it matched.</summary>
            public List<(ValueCandidate Candidate, string Op)> Filters = new List<(ValueCandidate, string)>();
            public List<string> Unsure = new List<string>();
            /// <summary>Plain lines shown beside a reading that did run: a comparison shown side by side, a period read.</summary>
            public List<string> Notes = new List<string>();
            /// <summary>The other good reading of each part Jev was torn over, the top one having been taken.</summary>
            public List<OtherReading> Others = new List<OtherReading>();
            /// <summary>Jev said, sure, that the model cannot answer this.</summary>
            public bool DoesNotFit;
            /// <summary>How many splits Jev was sure the question names; the most there can be when it was not sure.</summary>
            public int Splits = -1;
            /// <summary>Jev was not sure how many splits: each is asked with a "none" way out.</summary>
            public bool SplitsOpen;
            /// <summary>The order Jev was sure of, applied once the splits are known.</summary>
            public string Sort;
            /// <summary>Two values of one column may be compared: the split question keeps a way out, since the
            /// comparison adds its own split (ApplyCompare).</summary>
            public bool Compares;
            /// <summary>Two values or two periods really are shown side by side, each split on its own row.</summary>
            public bool SideBySide;
            /// <summary>How many years the data holds, when read; null when it was not.</summary>
            public int? Years;
            /// <summary>The periods the time words were read as, without dates (StatedReading tells them to Jev that way).</summary>
            public List<PeriodFact> Periods = new List<PeriodFact>();
            /// <summary>A top few over two splits ranks the pairs (SayTopOverPairs said so in a note).</summary>
            public bool TopOverPairs;
            /// <summary>What the reading assumed where Jev was not sure, one plain line per part (K16). The answer tells the first
            /// two in one line (Compose); each part that can be changed back is one of <see cref="Others"/>.</summary>
            public List<Assumed> Assumptions = new List<Assumed>();
            /// <summary>The split COUNT was below the bar (unlike <see cref="SplitsOpen"/>, which a comparison also sets), so a
            /// split of any slot is an optional part.</summary>
            public bool CountUnsure;
            /// <summary>Split slots whose table was a guess, settled once the third call names the column: left out and offered
            /// (an optional split), or run and said (the question's own).</summary>
            public HashSet<int> PendingAdditions = new HashSet<int>(), PendingAssumed = new HashSet<int>();
        }

        /// <summary>One part the reading assumed, and the plain line that says so.</summary>
        internal sealed class Assumed
        {
            public string Key, Line;
            public int Rank;
        }

        /// <summary>One time phrase of the question as the engine read it: which unit, which span of the data, and the
        /// filters it added to the query. No date and no label: only what span of the data the words mean.</summary>
        internal sealed class PeriodFact
        {
            /// <summary>day, week, month, quarter or year.</summary>
            public string Unit;
            /// <summary>The phrase as typed ("last year").</summary>
            public string Words;
            /// <summary>latest (the period the data is in), before (the one before it), todate-before (the earlier period up to
            /// the same day as the last day the data holds), or split (not pinned: every period is shown, and the person reads
            /// off the one meant).</summary>
            public string Span;
            /// <summary>The filters this phrase added to the query, so the reading does not tell them again as narrowing.</summary>
            public List<QueryFilter> Filters = new List<QueryFilter>();
        }

        /// <summary>One part Jev was torn over: the top pick was taken, and this is the other, of the same kind. The engine
        /// turns it into the whole question with that one part changed (LocalEngine.Query.cs, OthersOf).</summary>
        internal sealed class OtherReading
        {
            /// <summary>measure (every place the number is used), top_by, split, filter, or compare (twin columns); or a part the
            /// reading left out or assumed (K16): add_measure, add_split, add_filter (ToCandidate by Op), drop_filter (FromColumn).</summary>
            public string Kind;
            /// <summary>For add_filter: how the left-out narrowing is used.</summary>
            public string Op;
            public QueryMeasure FromMeasure, ToMeasure;
            public QueryColumn FromColumn, ToColumn;
            /// <summary>For a filter: the twin candidate, whose own matched values the other reading filters by.</summary>
            public ValueCandidate ToCandidate;
            /// <summary>For a comparison on twin columns: every value the other table's column is filtered by.</summary>
            public List<object> ToValues;
            public string Line;
            public string Instead;
        }

        /// <summary>The option Jev leaned to next, when it is of the same kind as its pick and the two together clear the
        /// bar while the pick alone does not. Null otherwise: then the pick stands or falls on its own.</summary>
        private static string Torn(DecisionAnswer a, double bar, Func<string, bool> sameKind)
        {
            if (a == null || a.Confidence >= bar || !sameKind(a.Choice)) return null;
            var second = a.Probabilities.Where(kv => kv.Key != a.Choice && sameKind(kv.Key)).OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (second.Key == null || Prob(a, a.Choice) + second.Value < bar) return null;
            // The pick must lead the way out too: a pick behind "none" or "another column" is not a good reading.
            if (a.Probabilities.Any(kv => !sameKind(kv.Key) && kv.Value > Prob(a, a.Choice))) return null;
            return second.Key;
        }

        /// <summary>What a below-bar answer to an optional part offers: the option Jev leaned to when it is a real one, and, when it
        /// leaned to "none", the best real option that holds at least 1 - bar of its weight (the weight that makes the "none"
        /// unsure). Null when there is nothing to offer. Ported from Graphicus (Kane, 3 Oct).</summary>
        internal static string Leaned(DecisionAnswer a, double bar, Func<string, bool> real)
        {
            if (a == null) return null;
            if (a.Choice != None) return real(a.Choice) ? a.Choice : null;
            var best = a.Probabilities.Where(kv => kv.Key != None && real(kv.Key)).OrderByDescending(kv => kv.Value).FirstOrDefault();
            return best.Key != null && best.Value >= 1 - bar ? best.Key : null;
        }

        public static Reading Read(DecisionAnswers answers, double bar, Request r, QueryCatalog cat)
        {
            var reading = new Reading();
            QueryMeasure MeasureOf(string id) => r.Measures.FirstOrDefault(m => MeasureId(m) == id);
            var fits = answers?.For(Fits);
            if (fits != null && fits.Noul == false && fits.Confidence >= bar)
            {
                // The one stop left (K16): the closest numbers are named from Jev's own pick first, each one ask away.
                reading.DoesNotFit = true;
                if (MeasureOf(answers?.For(MeasureQ(1))?.Choice) is { } leaned) reading.Spec.Measures.Add(leaned);
                return reading;
            }
            // A lean to "does not fit" below the bar was once thrown away, then stopped the reading. It is said now, and the best
            // guess runs (K16).
            if (fits != null && fits.Noul == false) Assume(reading, "fits", "This model may not hold exactly what you asked.", RankCaveat);

            // A pick below the bar is Jev's best guess (Graphicus, Kane 27 Sep; Semanticus K16, 8 Oct): it is kept, and the
            // reading says what it assumed. Only a part Jev did not answer at all is null.
            DecisionAnswer Taken(string question) => answers?.For(question);

            var first = Taken(MeasureQ(1));
            for (var slot = 1; slot <= MeasureSlots; slot++)
            {
                // Re-picking a measure already chosen, at any confidence, is no further number, not doubt about it.
                var again = Taken(MeasureQ(slot));
                if (slot > 1 && again != null && reading.Spec.Measures.Any(m => MeasureId(m) == again.Choice)) continue;
                // Torn between two real measures on the first number: the top one, and the other named.
                var other = slot == 1 ? Torn(again, bar, id => MeasureOf(id) != null) : null;
                if (other != null)
                {
                    var top = MeasureOf(again.Choice);
                    reading.Spec.Measures.Add(top);
                    Assume(reading, "measure", $"Using {top.Name}.", RankMeasure);
                    reading.Others.Add(new OtherReading
                    {
                        Kind = "measure", FromMeasure = top, ToMeasure = MeasureOf(other), Instead = MeasureOf(other).Name,
                        Line = $"Showing {top.Name}. You may have meant {MeasureOf(other).Name}.",
                    });
                    continue;
                }
                if (again == null) continue;
                if (slot > 1)
                {
                    // A SECOND number is optional (Graphicus, Kane 3 Oct): sure, it runs; below the bar it is left out and
                    // offered, one ask away, never run on a guess.
                    if (again.Confidence >= bar)
                    {
                        if (MeasureOf(again.Choice) is { } sure && !reading.Spec.Measures.Contains(sure)) reading.Spec.Measures.Add(sure);
                        continue;
                    }
                    var also = MeasureOf(Leaned(again, bar, id => MeasureOf(id) is { } l && !reading.Spec.Measures.Contains(l)));
                    if (also != null && reading.Spec.Measures.Count > 0)
                        reading.Others.Add(new OtherReading
                        {
                            Kind = "add_measure", ToMeasure = also, Instead = reading.Spec.Measures[0].Name + " and " + also.Name,
                            Line = $"You may also want {also.Name}.",
                        });
                    continue;
                }
                var m = MeasureOf(again.Choice);
                // A model with one number offers "none" on the first: that one number is still the best guess.
                if (m == null && r.Measures.Count == 1) m = r.Measures[0];
                if (m == null) { reading.Unsure.Add("Not sure which number you want."); continue; }
                reading.Spec.Measures.Add(m);
                if (again.Confidence >= bar && again.Choice == MeasureId(m)) continue;
                Assume(reading, "measure", $"Using {m.Name}.", RankMeasure);
                // The number Jev leaned to next is one ask away.
                var next = again.Probabilities.Where(kv => kv.Key != MeasureId(m) && MeasureOf(kv.Key) != null && kv.Value > 0)
                    .OrderByDescending(kv => kv.Value).Select(kv => MeasureOf(kv.Key)).FirstOrDefault();
                if (next != null)
                    reading.Others.Add(new OtherReading
                    {
                        Kind = "measure", FromMeasure = m, ToMeasure = next, Instead = next.Name,
                        Line = $"Showing {m.Name}. You may have meant {next.Name}.",
                    });
            }
            // How many splits. Unsure is not a stop: the second call then asks each split with a "none" way out, and a split it
            // is still not sure of is offered, not run (CountUnsure). With no answer at all, the words' own marker decides.
            var splits = Taken(SplitsQ);
            if (splits != null && int.TryParse(splits.Choice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k))
            {
                if (splits.Confidence >= bar) reading.Splits = Math.Min(k, GroupSlots);
                else { reading.Splits = GroupSlots; reading.SplitsOpen = true; reading.CountUnsure = true; }
            }
            else reading.Splits = SplitMarker.IsMatch(r.Words ?? "") ? 1 : 0;

            // A MEASURE'S NAME BEATS A VALUE (K16, Kane 8 Oct; the "used words" rule of Graphicus's word reader): 'sales' in
            // "sales by year" is the number asked for, even when a column (Metric Category) holds a value "Sales". It never
            // narrows that column, whatever Jev said; the narrowing is one ask away.
            var cands = new List<ValueCandidate>();
            foreach (var c in r.Candidates)
            {
                if (!NamesTheNumber(c, reading.Spec.Measures, r.Words)) { cands.Add(c); continue; }
                var said = answers?.For(FilterQ(c.Id));
                var col = Shown(c.Column, cat);
                if (said != null && said.Choice == None && said.Confidence >= bar) continue;
                if (reading.Others.Any(o => o.Kind == "add_filter" && o.ToCandidate?.Column == c.Column)) continue;
                Assume(reading, "filter:" + c.Id, $"{col} is not filtered.", RankFilter);
                reading.Others.Add(AddFilter(c, QueryOps.Is, cat));
            }

            // "coast vs north": both kept, and the column they share is a split of its own (ApplyCompare adds it). The
            // count Jev gave is kept: it once subtracted the comparison, and when Jev had counted only "by category" the
            // category was dropped. The split question keeps a way out instead (Compares).
            var compared = CompareGroups(r.Words, cands);
            reading.Compares = MayCompare(cands);
            if (reading.Compares && reading.Splits > 0) reading.SplitsOpen = true;
            foreach (var g in compared) foreach (var c in g) reading.Filters.Add((c, QueryOps.Is));

            var twins = TwinGroups(cands, r.Words);
            var comparedFlat = compared.SelectMany(g => g).ToList();
            var partners = Partners(cands, twins, comparedFlat);
            // A family: the twin groups of one column name, and the words in one table only that share it.
            var families = TwinFamilies(twins.Concat(partners).ToList());
            foreach (var family in families)
            {
                // Two or more words in twin columns: ONE table for all of them, the one Jev leaned to over every word, so
                // Coast on one table never sits beside North on the other; the other table is named, one ask away.
                var said = string.Join(" and ", family.Select(g => "'" + g[0].Words + "'"));
                var names = string.Join(" or ", family[0].Select(c => Shown(c.Column, cat)));
                var key = "filter:" + family[0][0].Id;
                var picks = family.Select(g => (Group: g, Answer: answers?.For(FilterQ(g[0].Id)))).ToList();
                if (picks.Any(x => x.Answer == null || x.Answer.Choice == None))
                {
                    if (picks.All(x => x.Answer != null && x.Answer.Choice == None && x.Answer.Confidence >= bar)) continue;
                    Assume(reading, key, $"{names} is not filtered.", RankFilter);
                    continue;
                }
                var op = picks[0].Answer.Choice.Split('|')[0];
                // A twin group's options name the column (op|id); a partner in one table was asked the plain way (op).
                double P((List<ValueCandidate> Group, DecisionAnswer Answer) x, ValueCandidate c) =>
                    x.Group.Count > 1 || x.Answer.Choice.Contains('|') ? Prob(x.Answer, op + "|" + c.Id) : Prob(x.Answer, op);
                var unsure = picks.Any(x => x.Answer.Choice.Split('|')[0] != op || x.Group.Sum(c => P(x, c)) < bar);
                double Weight(string table) => picks.Sum(x => x.Group.Where(c => c.Column.Table == table).Sum(c => P(x, c)));
                // Only a table holding every word can be read: 'south' is in Store Region alone, so "south and coast" is
                // read there even when Jev leaned to Customer Region for 'coast'.
                var tables = family.SelectMany(g => g.Select(c => c.Column.Table)).Distinct(StringComparer.Ordinal)
                    .Where(t => family.All(g => g.Any(c => c.Column.Table == t))).OrderByDescending(Weight).ToList();
                if (tables.Count == 0) { Assume(reading, key, $"{names} is not filtered: no one table holds {said}.", RankFilter); continue; }
                var on = family.Select(g => g.First(c => c.Column.Table == tables[0])).ToList();
                foreach (var c in on) reading.Filters.Add((c, op));
                if (unsure) Assume(reading, key, $"Read {said} as {Shown(on[0].Column, cat)}.", RankFilter);
                var to = tables.Count < 2 ? null : family.Select(g => g.FirstOrDefault(c => c.Column.Table == tables[1])).ToList();
                if (to == null || to.Any(c => c == null)) continue;
                reading.Others.Add(new OtherReading
                {
                    Kind = "compare", FromColumn = on[0].Column, ToColumn = to[0].Column, ToValues = to.SelectMany(c => c.Values).Distinct().ToList(),
                    Instead = Shown(to[0].Column, cat),
                    Line = $"Showing {Shown(on[0].Column, cat)} for {said}. You may have meant {Shown(to[0].Column, cat)}.",
                });
            }

            // Twins: one question for the pair, the pair's weight added. One filter runs; a torn pair names the other.
            foreach (var g in twins.Where(g => !families.Any(f => f.Contains(g))))
            {
                var a = answers?.For(FilterQ(g[0].Id));
                if (a == null) continue;
                var names = string.Join(" or ", g.Select(c => Shown(c.Column, cat)));
                var key = "filter:" + g[0].Id;
                if (a.Choice == None)
                {
                    if (a.Confidence < bar) Assume(reading, key, $"{names} is not filtered.", RankFilter);
                    continue;
                }
                var op = a.Choice.Split('|')[0];
                var picked = g.FirstOrDefault(c => a.Choice == op + "|" + c.Id);
                if (picked == null) continue;
                reading.Filters.Add((picked, op));
                if (a.Confidence >= bar) continue;
                Assume(reading, key, $"Read '{picked.Words}' as {Shown(picked.Column, cat)}.", RankFilter);
                var twin = g.Where(c => c != picked).OrderByDescending(c => Prob(a, op + "|" + c.Id)).First();
                reading.Others.Add(new OtherReading
                {
                    Kind = "filter", FromColumn = picked.Column, ToColumn = twin.Column, ToCandidate = twin, Instead = Shown(twin.Column, cat),
                    Line = $"Showing {Shown(picked.Column, cat)} {OpWords(op)} '{picked.Words}'. You may have meant {Shown(twin.Column, cat)}.",
                });
            }

            // The plain matches. A below-bar pick is the best guess for the FIRST narrowing the words name; a later one is an
            // extra, left out and offered (LeaveOutExtraFilters, the Graphicus rule of 3 Oct).
            var asked = new HashSet<ValueCandidate>(twins.SelectMany(g => g).Concat(comparedFlat).Concat(partners.SelectMany(g => g)));
            var plain = new List<(ValueCandidate Candidate, DecisionAnswer Answer)>();
            foreach (var cand in cands.Where(c => !asked.Contains(c) && !Covered(c, comparedFlat)))
            {
                var a = answers?.For(FilterQ(cand.Id));
                if (a == null) continue;
                if (a.Choice == None && a.Confidence >= bar)
                {
                    // A year or date the person typed that Jev says narrows nothing (2023 in data that holds 2024 and 2025) would
                    // show the all-time total in silence: said, with the narrowing one ask away (K16).
                    if (QueryGuards.IsCalendar(cand.Column) && (cand.Words ?? "").Any(char.IsDigit))
                    {
                        Assume(reading, "filter:" + cand.Id, $"{Shown(cand.Column, cat)} is not filtered by '{cand.Words}'.", RankFilter);
                        reading.Others.Add(AddFilter(cand, QueryOps.Is, cat));
                    }
                    continue;
                }
                plain.Add((cand, a));
            }
            LeaveOutExtraFilters(reading, plain, bar, cat);

            if (r.Numbers.Count > 0)
            {
                var top = Taken(TopQ);
                if (top != null && top.Choice != None && int.TryParse(top.Choice.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    reading.Spec.Top = n;
                    var byAnswer = Taken(TopByQ);
                    var otherBy = Torn(byAnswer, bar, id => MeasureOf(id) != null);
                    // A ranking number Jev did not name, or named "none", is the first number.
                    var byMeasure = MeasureOf(byAnswer?.Choice) ?? reading.Spec.Measures.FirstOrDefault();
                    if (otherBy != null && byMeasure != null)
                        reading.Others.Add(new OtherReading
                        {
                            Kind = "top_by", FromMeasure = byMeasure, ToMeasure = MeasureOf(otherBy), Instead = MeasureOf(otherBy).Name,
                            Line = $"Ranked by {byMeasure.Name}. You may have meant {MeasureOf(otherBy).Name}.",
                        });
                    // Sure what ranks a top N, that is the number, even when the first measure question was not sure of it.
                    if (byMeasure != null && byAnswer != null && byAnswer.Confidence >= bar && first != null && first.Confidence < bar
                        && (first.Choice == None || first.Choice == MeasureId(byMeasure)))
                        reading.Assumptions.RemoveAll(x => x.Key == "measure" && x.Line == $"Using {byMeasure.Name}.");
                    reading.Spec.TopBy = byMeasure;
                    if (byMeasure != null && !reading.Spec.Measures.Contains(byMeasure)) reading.Spec.Measures.Add(byMeasure);
                    // Which end: Jev's pick; with none, the words' own (bottom, worst, least), else the top.
                    var end = Taken(TopEndQ);
                    reading.Spec.Lowest = end != null ? end.Choice == "lowest" : BottomWords.IsMatch(r.Words ?? "");
                    if (top.Confidence < bar) Assume(reading, "top", $"Showing the {(reading.Spec.Lowest ? "bottom" : "top")} {n.ToString(CultureInfo.InvariantCulture)}.", RankTop);
                }
            }
            // The order changes no number, and the statement names it ("with the highest Net Sales first"), so a pick that is short of the
            // bar but more likely than not is taken and said; a torn one takes the default. Dropping it left "Rank states by revenue per
            // unit" unsorted, and the check rightly asked back (round 7). Applied once the splits are known.
            var sort = Taken(SortQ);
            if (sort != null && sort.Confidence >= Math.Min(bar, SortBar)) reading.Sort = sort.Choice;
            return reading;
        }

        private static readonly Regex BottomWords = new Regex(@"\b(bottom|worst|lowest|smallest|least|fewest)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ---- the assumptions (K16, Kane 8 Oct) ------------------------------------------------------------------

        /// <summary>The order the assumption line tells them in: a doubt about the whole number first, then the number, the
        /// narrowing, the split and the top few. The line keeps the first two (Compose).</summary>
        internal const int RankCaveat = 0, RankMeasure = 1, RankFilter = 2, RankSplit = 3, RankTop = 4;

        /// <summary>Say what the reading assumed about one part. A part says it once.</summary>
        internal static void Assume(Reading reading, string key, string line, int rank)
        {
            if (string.IsNullOrWhiteSpace(line) || reading.Assumptions.Any(a => a.Key == key)) return;
            reading.Assumptions.Add(new Assumed { Key = key, Line = line.Trim(), Rank = rank });
        }

        /// <summary>The one assumption line (the principle of 8 Oct: a number and at most one plain line): the first two
        /// assumptions by rank, in the order they were made. Null when nothing was assumed.</summary>
        internal static string Compose(Reading reading)
        {
            var lines = reading.Assumptions.Select((a, i) => (a, i)).OrderBy(x => x.a.Rank).ThenBy(x => x.i)
                .Select(x => x.a.Line).Distinct(StringComparer.Ordinal).Take(2).ToList();
            return lines.Count == 0 ? null : string.Join(" ", lines);
        }

        /// <summary>The words a match was made on are the name of a number the reading uses ('sales' in Net Sales, or a synonym
        /// of it the question said), and they come once in the question. Plurals count; other slips do not.</summary>
        internal static bool NamesTheNumber(ValueCandidate c, IEnumerable<QueryMeasure> measures, string words)
        {
            if (c == null || c.Kind != "value") return false;
            var said = FuzzyWords.Words(c.Words ?? "").Select(w => w.Word).ToList();
            if (said.Count == 0) return false;
            var asked = FuzzyWords.Words(words ?? "").Select(w => w.Word).ToList();
            if (said.Any(w => asked.Count(a => a == w) > 1)) return false;
            static bool Same(string a, string b) => a == b || a == b + "s" || b == a + "s" || a == b + "es" || b == a + "es";
            return measures.Any(m =>
            {
                var name = QuerySplitShortlist.NameWords(m.Name).Concat(SaidSynonyms(words, m.Synonyms).SelectMany(QuerySplitShortlist.NameWords)).ToList();
                return said.All(w => name.Any(n => Same(n, w)));
            });
        }

        /// <summary>The narrowing a reading left out, one ask away: the match's own values, by the op given.</summary>
        internal static OtherReading AddFilter(ValueCandidate c, string op, QueryCatalog cat)
        {
            var col = Shown(c.Column, cat);
            var value = c.Values.Count > 0 ? Convert.ToString(c.Values[0], CultureInfo.InvariantCulture) : c.Words;
            return new OtherReading
            {
                Kind = "add_filter", ToCandidate = c, Op = op,
                Instead = op == QueryOps.Is ? $"only {col} {value}" : op == QueryOps.IsNot ? $"{col} without {value}" : $"{col} {OpWords(op)} {value}",
                Line = $"{col} is not filtered.",
            };
        }

        /// <summary>The FIRST narrowing the words name is the question's own: a below-bar pick of it runs as the best guess and is
        /// said, and a below-bar "none" leaves it out and says so, one ask from adding it. Every LATER narrowing is an extra: a
        /// sure one runs; a below-bar one is left out, and the op Jev leaned to is one ask away (Graphicus, Kane 3 Oct: an
        /// optional part Jev is not sure to add is offered, never run).</summary>
        private static void LeaveOutExtraFilters(Reading reading, List<(ValueCandidate Candidate, DecisionAnswer Answer)> plain, double bar, QueryCatalog cat)
        {
            var mainSeen = reading.Filters.Count > 0;
            foreach (var (cand, a) in plain.OrderBy(x => x.Candidate.Start))
            {
                var col = Shown(cand.Column, cat);
                var key = "filter:" + cand.Id;
                var sure = a.Confidence >= bar;
                var main = !mainSeen;
                if (a.Choice != None)
                {
                    if (sure || main)
                    {
                        reading.Filters.Add((cand, a.Choice));
                        mainSeen = true;
                        if (sure) continue;
                        Assume(reading, key, a.Choice == QueryOps.Is ? $"Showing only {cand.Words} in {col}." : $"Showing only {col} {OpWords(a.Choice)} {cand.Words}.", RankFilter);
                        reading.Others.Add(new OtherReading { Kind = "drop_filter", FromColumn = cand.Column, Instead = "every " + col, Line = $"Showing only {cand.Words} in {col}." });
                        continue;
                    }
                    reading.Others.Add(AddFilter(cand, a.Choice, cat));
                    continue;
                }
                // A below-bar "none": left out. On the first narrowing the words name it is said, with the op Jev leaned to (or
                // "is") one ask away; on an extra the op is offered only when Jev leaned to it enough.
                var op = Leaned(a, bar, id => id != None);
                if (main) Assume(reading, key, $"{col} is not filtered.", RankFilter);
                if (main || op != null) reading.Others.Add(AddFilter(cand, op ?? QueryOps.Is, cat));
            }
        }

        /// <summary>Two values of one column the words name, both kept ("coast vs north", "did 2025 beat 2024", "coast and
        /// north"): split by their column too, so they are shown side by side, and say so. Never one total for both. With
        /// no room left for the split, nothing runs and the answer says why.</summary>
        public static void ApplyCompare(Reading reading, QueryCatalog cat)
        {
            foreach (var g in reading.Filters.Where(f => f.Op == QueryOps.Is).GroupBy(f => f.Candidate.Column))
            {
                var words = g.Select(f => f.Candidate.Words).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var col = g.Key;
                if (words.Count < 2 || reading.Spec.GroupBy.Contains(col)) continue;
                var said = string.Join(" and ", words.Select(w => "'" + w + "'"));
                // Split by the same-named column of another table (Customer Region beside Store Region = Coast or North):
                // the compared table's column takes its place, so one table is read, and the other stays one ask away.
                var twin = reading.Spec.GroupBy.FirstOrDefault(c => c.Kind == col.Kind && string.Equals(c.Name, col.Name, StringComparison.OrdinalIgnoreCase));
                if (twin != null)
                {
                    reading.Spec.GroupBy[reading.Spec.GroupBy.IndexOf(twin)] = col;
                    if (reading.Spec.SortColumn == twin) reading.Spec.SortColumn = col;
                    reading.Notes.Add($"Showing {said} side by side, split by {Shown(col, cat)}.");
                    reading.SideBySide = true;
                    continue;
                }
                if (reading.Spec.GroupBy.Count >= GroupSlots)
                {
                    // No room for the split: the last split gives way, so the compared values are never added into one total (K16:
                    // answer, and say what was left out).
                    var gone = reading.Spec.GroupBy[reading.Spec.GroupBy.Count - 1];
                    reading.Spec.GroupBy.Remove(gone);
                    if (reading.Spec.SortColumn == gone) reading.Spec.SortColumn = null;
                    Assume(reading, "compare:" + col.Id, $"Not split by {Shown(gone, cat)}, so {said} show side by side.", RankCaveat);
                }
                reading.Spec.GroupBy.Add(col);
                reading.Notes.Add($"Showing {said} side by side, split by {Shown(col, cat)}.");
                reading.SideBySide = true;
            }
            // Already split by the compared column (the question said "by region" too): side by side all the same.
            if (reading.Filters.Where(f => f.Op == QueryOps.Is).GroupBy(f => f.Candidate.Column)
                .Any(g => g.Select(f => f.Candidate.Words).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2 && reading.Spec.GroupBy.Contains(g.Key)))
                reading.SideBySide = true;
        }

        /// <summary>A top few over two splits ranks the pairs (a product in a region), not the top few within each: said, so
        /// three rows from one region are never read as each region's top three. Year beside a month or a quarter of the
        /// year names one period, so ranking those pairs is ranking the periods, and nothing needs saying.</summary>
        public static void SayTopOverPairs(Reading reading, QueryCatalog cat)
        {
            var s = reading.Spec;
            if (!(s.Top > 0) || s.GroupBy.Count < 2) return;
            if (s.GroupBy.Any(QueryPeriods.IsYear) && s.GroupBy.Any(c => QueryPeriods.IsMonthOfYear(c) || QueryPeriods.IsQuarter(c))) return;
            var a = Shown(s.GroupBy[0], cat);
            var b = Shown(s.GroupBy[1], cat);
            reading.TopOverPairs = true;
            reading.Notes.Add($"The {(s.Lowest ? "bottom" : "top")} {s.Top.Value.ToString(CultureInfo.InvariantCulture)} is taken over every {a} and {b} pair together, "
                + $"not within each {b}, so one {b} can fill every row.");
        }

        /// <summary>Apply the order Jev chose, once the measures and the splits are settled.</summary>
        public static void ApplySort(Reading reading)
        {
            if (reading.Sort != null && reading.Spec.Top == null)
            {
                switch (reading.Sort)
                {
                    case "measure_high": reading.Spec.SortMeasure = reading.Spec.Measures.FirstOrDefault(); reading.Spec.Descending = true; break;
                    case "measure_low": reading.Spec.SortMeasure = reading.Spec.Measures.FirstOrDefault(); break;
                    case "group_a_z": reading.Spec.SortColumn = reading.Spec.GroupBy.FirstOrDefault(); break;
                    case "group_z_a": reading.Spec.SortColumn = reading.Spec.GroupBy.FirstOrDefault(); reading.Spec.Descending = true; break;
                }
            }
        }

        // ---- the split, asked second -------------------------------------------------------------------------

        /// <summary>The second call: for each split, which of the few shortlisted columns (each reaches every chosen
        /// measure, and each says which of the question's words matched it), or another one, and then which table.
        /// An empty shortlist asks the table alone, and a model with no column to split by asks nothing.</summary>
        public static Request BuildSplit(string words, QueryCatalog cat, Reading first)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.QuerySummarize);
            var measures = first.Spec.Measures;
            var r = new Request { Words = (words ?? "").Trim(), Measures = measures.ToList() };
            // A money amount on each row (NetSalesAmount) is a number to add up, not a thing to split by, and offered as a
            // split it drew weight from the column meant.
            var listed = QuerySplitShortlist.WithWhy(r.Words, cat, measures).Where(x => x.Column.Kind != "decimal").ToList();
            r.Columns = listed.Select(x => x.Column).ToList();
            // The way out lists every table with a column a person can see, reachable or not: a split the measure
            // cannot reach is then refused by the reach check with a plain reason, not left as a vague "not sure".
            r.Tables = cat.Visible.Where(c => !c.Key).Select(c => c.Table).Distinct(StringComparer.Ordinal).ToList();
            var q = new List<DecisionQuestion>();
            // No column a person can see to split by: no table to offer either, so nothing is asked (ReadSplit says so).
            for (var slot = 1; slot <= first.Splits && r.Tables.Count > 0; slot++)
            {
                var options = listed.Select(x => new DecisionOption(ColumnId(x.Column),
                        "the question splits the numbers by " + x.Column.Table + " " + x.Column.Name + " (" + x.Why + ")"))
                    .Append(new DecisionOption(Other, "none of these: the question splits by another column"));
                if (first.SplitsOpen)
                    options = options.Append(new DecisionOption(None, first.Compares
                        ? (slot == 1 ? "the question splits by nothing beyond the values it compares" : "the question splits its numbers only one way, beside the values it compares")
                        : slot == 1 ? "the question asks for one total, not split by anything" : "the question splits its numbers only one way"));
                // An empty shortlist with a sure count leaves the way out as the only option. A question with one answer
                // asks nothing, and the contract refuses it, which once stopped the whole reading as "Jev was not asked"
                // (the fair-model audit, 27 Sep). The engine already knows that answer, so the split goes straight to the
                // table, whose "none of these tables" keeps a real way out.
                if (listed.Count > 0 || first.SplitsOpen)
                    q.Add(new DecisionQuestion(GroupQ(slot), DecisionQuestionKind.Choice,
                        first.Splits == 1 && !first.SplitsOpen ? "Which column does the question split its numbers by? Any listed column that fits the words is a right answer."
                        : slot == 1 ? "Which column is the FIRST thing the question splits its numbers by?"
                                    : "Which column is the SECOND thing the question splits its numbers by?",
                        options));
                q.Add(new DecisionQuestion(GroupTableQ(slot), DecisionQuestionKind.Choice,
                    slot == 1 ? "Which table holds the column the question splits its numbers by (the FIRST, if it splits two ways)?"
                              : "Which table holds the SECOND thing the question splits its numbers by?",
                    r.Tables.Select(t => new DecisionOption(TableId(t), "the split is a column of " + t))
                        .Append(new DecisionOption(None, "none of these tables"))));
            }
            r.Questions = q;
            r.Snapshot = SplitSnapshot(point, r, cat.Visible.Where(c => !c.Key && r.Tables.Contains(c.Table)));
            return r;
        }

        /// <summary>The third call, only when the shortlist missed: which column of the chosen table. A table can show
        /// more columns than one call lists, and here the columns handed in ARE the ones the question offers, so the cut
        /// to MaxListed would fall on options Jev is then asked to pick blind, with no type, folder or description beside
        /// them. So the OFFER is cut, not the list: the columns whose names share the most of the question's words are
        /// kept (the rule the first call lists by), and two tables asked in one call share the room evenly.</summary>
        public static Request BuildInTable(string words, QueryCatalog cat, IReadOnlyList<QueryMeasure> measures, IReadOnlyList<(int Slot, string Table)> tables)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.QuerySummarize);
            var r = new Request { Words = (words ?? "").Trim(), Measures = measures.ToList() };
            var named = Namer(r.Words);
            var asked = tables.Select(x => x.Table).Distinct(StringComparer.Ordinal).ToList();
            var offer = asked.ToDictionary(t => t, t => cat.Visible.Where(c => c.Table == t && !c.Key).ToList(), StringComparer.Ordinal);
            var room = Shares(asked.Select(t => offer[t].Count).ToArray(), MaxListed);
            for (var i = 0; i < asked.Count; i++)
            {
                var cols = offer[asked[i]];
                if (cols.Count <= room[i]) continue;
                var keep = new HashSet<QueryColumn>(cols.Select((c, at) => (c, at))
                    .OrderByDescending(x => named(x.c.Name, x.c.Synonyms)).ThenBy(x => x.at).Take(room[i]).Select(x => x.c));
                offer[asked[i]] = cols.Where(keep.Contains).ToList();
            }

            var q = new List<DecisionQuestion>();
            foreach (var (slot, table) in tables)
            {
                var cols = offer[table];
                r.Columns.AddRange(cols.Where(c => !r.Columns.Contains(c)));
                // Nothing left to offer is not asked as a one-option question: the contract refuses one, and the reading
                // then says plainly that it is not sure which column of the table to split by.
                if (cols.Count == 0) continue;
                q.Add(new DecisionQuestion(GroupInQ(slot), DecisionQuestionKind.Choice,
                    $"Which column of {table} does the question split its numbers by?",
                    cols.Select(c => new DecisionOption(ColumnId(c), "the question splits the numbers by " + c.Table + " " + c.Name))
                        .Append(new DecisionOption(None, "none of these columns"))));
            }
            r.Questions = q;
            r.Snapshot = SplitSnapshot(point, r, r.Columns);
            return r;
        }

        /// <summary>How much of one call's room each table asked about gets, when together they show more columns than the
        /// call lists: an equal share, with the room a small table does not need handed on to the bigger ones.</summary>
        private static int[] Shares(int[] counts, int room)
        {
            var take = new int[counts.Length];
            var left = room;
            var order = Enumerable.Range(0, counts.Length).OrderBy(i => counts[i]).ToList();
            for (var n = order.Count; n > 0; n--)
            {
                var i = order[order.Count - n];
                take[i] = Math.Min(counts[i], left / n);
                left -= take[i];
            }
            return take;
        }

        /// <summary>The split calls send what the first call sends about each field, descriptions included (the
        /// Graphicus verifier, 27 Sep: a description that settles "the store's region, not the customer's" was sent in
        /// the call that does not pick the split and left out of the one that does). Every column offered is listed.</summary>
        private static IReadOnlyDictionary<string, object> SplitSnapshot(DecisionPoint point, Request r, IEnumerable<QueryColumn> columns)
        {
            var all = columns.ToList();
            var keep = new HashSet<QueryColumn>(all.Where(r.Columns.Contains).Concat(all.Where(c => !r.Columns.Contains(c))).Take(MaxListed));
            var listed = all.Where(keep.Contains).ToList();
            var snapshot = SplitSnapshot(point, r, listed, withDescriptions: true);
            try { DecisionSnapshot.RequireWithin(snapshot, 60 * 1024); }
            catch (InvalidOperationException) { snapshot = SplitSnapshot(point, r, listed, withDescriptions: false); }
            return snapshot;
        }

        private static IReadOnlyDictionary<string, object> SplitSnapshot(DecisionPoint point, Request r, List<QueryColumn> columns, bool withDescriptions)
        {
            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("question_words", r.Words),
                new KeyValuePair<string, object>("model_fields", new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "measures", r.Measures.Select(m => MeasureField(m, withDescriptions)).ToList() },
                    { "columns", columns.Select(c => ColumnField(c, withDescriptions)).ToList() },
                }),
                // The filters were settled in the first call; the point's fields are fixed, so this one goes empty.
                new KeyValuePair<string, object>("value_matches", new List<object>()),
            });
        }

        /// <summary>Read the second call. Returns the slots whose split is in a table still to be asked about. A split Jev is
        /// not sure of is its best guess and is said (K16); an OPTIONAL split (a second one, or any when the count was not sure)
        /// below the bar is left out and offered instead (Graphicus, Kane 3 Oct).</summary>
        public static List<(int Slot, string Table)> ReadSplit(DecisionAnswers answers, double bar, Request r, Reading reading, QueryCatalog cat)
        {
            var pending = new List<(int, string)>();
            if (reading.Splits > 0 && r.Tables.Count == 0) { Assume(reading, "split", NothingToSplitBy, RankSplit); return pending; }
            for (var slot = 1; slot <= reading.Splits; slot++)
            {
                var optional = SplitIsOptional(reading, slot);
                // Not asked because the shortlist was empty: the engine's own answer is the way out, another column.
                var columnAsked = r.Questions.Any(q => q.Name == GroupQ(slot));
                var a = answers?.For(GroupQ(slot));
                if (columnAsked)
                {
                    // Not sure how many splits: a later slot answered "none", or re-picking a split already chosen, at any
                    // confidence, is no further split (the rule measure slot 2 uses). It is not doubt about the first.
                    if (slot > 1 && reading.SplitsOpen && a != null
                        && ((a.Choice == None && a.Confidence >= bar) || reading.Spec.GroupBy.Any(g => ColumnId(g) == a.Choice))) continue;
                    var pick = a == null ? null : r.Columns.FirstOrDefault(c => ColumnId(c) == a.Choice);
                    // The same kind of column as the pick: its twin in another table (Store Region, Customer Region), or a
                    // column of the same table holding the same kind of value. Never another table's unrelated column.
                    bool SameKind(string id)
                    {
                        var c = r.Columns.FirstOrDefault(x => ColumnId(x) == id);
                        return c != null && pick != null && (c == pick || c.Kind == pick.Kind
                            && (c.Table == pick.Table || string.Equals(c.Name, pick.Name, StringComparison.OrdinalIgnoreCase)));
                    }
                    // Torn between the column two compared values sit in and "nothing beyond the comparison": both give the
                    // same answer, since the comparison adds that split itself (ApplyCompare).
                    if (reading.SplitsOpen && reading.Compares && a != null && (a.Choice == None || Compared(reading).Any(c => ColumnId(c) == a.Choice))
                        && Prob(a, None) + Compared(reading).Sum(c => Prob(a, ColumnId(c))) >= bar) continue;
                    if (a == null)
                    {
                        // Jev gave no answer for this split: the engine's own first column for these words, said.
                        if (!optional && r.Columns.FirstOrDefault(c => !reading.Spec.GroupBy.Contains(c)) is { } own) Split(reading, own, cat, assumed: true);
                        continue;
                    }
                    var other = pick == null ? null : Torn(a, bar, SameKind);
                    // Two readers agree: Jev leads by a clear margin and the pick is the engine's own first column for these words.
                    var agreed = ClearLead(a) && pick != null && r.Columns.Count > 0 && r.Columns[0] == pick;
                    var unsure = a.Confidence < bar && other == null && !agreed;
                    if (optional && unsure)
                    {
                        var leaned = Leaned(a, bar, id => r.Columns.Any(c => ColumnId(c) == id) && !reading.Spec.GroupBy.Any(g => ColumnId(g) == id));
                        if (leaned != null) reading.Others.Add(AddSplit(r.Columns.First(c => ColumnId(c) == leaned), reading, cat));
                        continue;
                    }
                    if (a.Choice == None)
                    {
                        if (unsure) Assume(reading, "split", "Shown as one total, not split.", RankSplit);
                        continue;
                    }
                    if (pick != null)
                    {
                        Split(reading, pick, cat, assumed: unsure || other != null);
                        if (other != null)
                        {
                            var to = r.Columns.First(c => ColumnId(c) == other);
                            reading.Others.Add(new OtherReading
                            {
                                Kind = "split", FromColumn = pick, ToColumn = to, Instead = Shown(to, cat),
                                Line = $"Split by {Shown(pick, cat)}. You may have meant {Shown(to, cat)}.",
                            });
                        }
                        continue;
                    }
                }
                var t = answers?.For(GroupTableQ(slot));
                var table = t == null ? null : r.Tables.FirstOrDefault(x => TableId(x) == t.Choice);
                if (table == null)
                {
                    // No table either: the engine's own first column for these words, said, when the split is the question's own;
                    // with none, one total, said.
                    if (optional) continue;
                    if (r.Columns.FirstOrDefault(c => !reading.Spec.GroupBy.Contains(c)) is { } own) Split(reading, own, cat, assumed: true);
                    else Assume(reading, "split", "Shown as one total, not split.", RankSplit);
                    continue;
                }
                if (t.Confidence < bar && !ClearLead(t))
                {
                    if (optional) reading.PendingAdditions.Add(slot);
                    else reading.PendingAssumed.Add(slot);
                }
                pending.Add((slot, table));
            }
            return pending;
        }

        private static readonly System.Text.RegularExpressions.Regex SplitMarker = new System.Text.RegularExpressions.Regex(
            @"\b(by|per|each|every|across|split|breakdown|broken\s+down|grouped)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>A pick that holds most of the mass and leads every other option by a clear margin (at least 0.6, and 2.5 times the
        /// runner-up), short of the bar. Never with no probabilities to see the margin by. Two twins near a tie are never this.</summary>
        internal static bool ClearLead(DecisionAnswer a)
        {
            if (a == null || a.Probabilities.Count == 0) return false;
            var top = Prob(a, a.Choice);
            var runner = a.Probabilities.Where(kv => kv.Key != a.Choice).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
            return top >= 0.6 && top >= 2.5 * runner;
        }

        /// <summary>The columns two or more of the words' values are kept in: the ones a comparison shows side by side.</summary>
        private static IEnumerable<QueryColumn> Compared(Reading reading) =>
            reading.Filters.Where(f => f.Op == QueryOps.Is).GroupBy(f => f.Candidate.Column)
                .Where(g => g.Select(f => f.Candidate.Words).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2).Select(g => g.Key);

        /// <summary>Read the third call. A column Jev is not sure of is its best guess and is said; with no answer, the table's
        /// column whose name shares the most of the question's words.</summary>
        public static void ReadInTable(DecisionAnswers answers, double bar, Request r, Reading reading, IReadOnlyList<(int Slot, string Table)> tables, QueryCatalog cat = null)
        {
            var named = Namer(r.Words);
            foreach (var (slot, table) in tables)
            {
                var a = answers?.For(GroupInQ(slot));
                var col = a == null ? null : r.Columns.FirstOrDefault(c => ColumnId(c) == a.Choice);
                var unsure = col == null || (a.Confidence < bar && !ClearLead(a));
                var optional = reading.PendingAdditions.Remove(slot) | SplitIsOptional(reading, slot);
                var assumed = reading.PendingAssumed.Remove(slot);
                if (optional && (unsure || assumed))
                {
                    if (col != null && !reading.Spec.GroupBy.Contains(col)) reading.Others.Add(AddSplit(col, reading, cat));
                    continue;
                }
                col ??= r.Columns.Where(c => c.Table == table && !reading.Spec.GroupBy.Contains(c))
                    .Select((c, at) => (c, at)).OrderByDescending(x => named(x.c.Name, x.c.Synonyms)).ThenBy(x => x.at).Select(x => x.c).FirstOrDefault();
                if (col == null) continue;
                Split(reading, col, cat, assumed: unsure || assumed);
            }
        }

        /// <summary>The split in this slot is an optional part: the question's split count was not sure, or it is a second slot.</summary>
        internal static bool SplitIsOptional(Reading reading, int slot) => reading.CountUnsure || slot > 1;

        /// <summary>Split by a column; a column already split by is no further split. A best guess says so.</summary>
        private static void Split(Reading reading, QueryColumn col, QueryCatalog cat, bool assumed)
        {
            if (reading.Spec.GroupBy.Contains(col)) return;
            reading.Spec.GroupBy.Add(col);
            if (assumed) Assume(reading, "split:" + col.Id, $"Split by {Shown(col, cat)}.", RankSplit);
        }

        /// <summary>A split the reading left out, one ask away.</summary>
        private static OtherReading AddSplit(QueryColumn col, Reading reading, QueryCatalog cat) => new OtherReading
        {
            Kind = "add_split", ToColumn = col,
            Instead = (reading.Spec.Measures.Count > 0 ? reading.Spec.Measures[0].Name + " " : "") + "by " + Shown(col, cat),
            Line = $"You may also want it split by {Shown(col, cat)}.",
        };

        internal static string Shown(QueryColumn c, QueryCatalog cat) => c == null ? "that column" : cat.Shown(c);

        internal static string OpWords(string op) => op switch
        {
            QueryOps.Is => "is",
            QueryOps.IsNot => "is not",
            QueryOps.After => "after",
            QueryOps.From => "from",
            QueryOps.Before => "before",
            QueryOps.UpTo => "up to",
            _ => op,
        };
    }
}
