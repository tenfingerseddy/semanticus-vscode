// The stated reading: what an answer shows, said once, from the query that will run (the ruling "Jev data questions v2",
// 29 Sep). ONE structure, built from the QuerySpec and the facts of how it was read (the words each value matched, the
// periods read, two values or two periods side by side, a top few over pairs), and rendered twice:
//
//   For the person (Person): model values and dates. "This shows Net Sales for each Region, where Year is 2025." The
//     first line of every answer on both doors, so anything that still gets through is visible, never silent.
//   For Jev (ForJev): the same structure in the question's OWN words. A filter value is the words the question held
//     ('coast'), never the model's value (Coast); a period is told without a date or a value ("the whole of the year
//     before the latest year the data holds"); "not narrowed by anything" when nothing narrows; and a fixed last sentence
//     saying what the shape never does. Nothing the question's words do not hold leaves the machine, and a test proves it.
//
// The engine builds both from the same fields, so the sentence a person reads and the sentence Jev judges can never be
// two different descriptions of the query. Jev writes none of it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Semanticus.Engine.Query
{
    internal sealed class StatedReading
    {
        /// <summary>The fixed last sentence of what Jev is told: what this shape never does of its own.</summary>
        internal const string ShowsOnly = "Apart from what is said above, it shows totals only. It works out no change of its own between periods, no share or percent of its own, and nothing about things with no rows.";

        private readonly QuerySpec _spec;
        private readonly JevQueryReader.Reading _reading;
        private readonly QueryCatalog _cat;

        private StatedReading(QuerySpec spec, JevQueryReader.Reading reading, QueryCatalog cat)
        {
            _spec = spec;
            _reading = reading;
            _cat = cat;
        }

        /// <summary>The reading that will run. <paramref name="reading"/> may be null for parts given as said: then there are
        /// no words the values matched and no period read, and only the person's rendering is used.</summary>
        public static StatedReading From(QuerySpec spec, JevQueryReader.Reading reading, QueryCatalog cat) => new StatedReading(spec, reading, cat);

        public QuerySpec Spec => _spec;

        // ---- the reading's own facts, and the gaps they clear --------------------------------------------------------

        /// <summary>The measures the query shows or ranks by.</summary>
        public IReadOnlyList<QueryMeasure> UsedMeasures =>
            _spec.Measures.Concat(new[] { _spec.TopBy, _spec.SortMeasure }.Where(m => m != null)).Distinct().ToList();

        /// <summary>The query is split by a column that holds periods: every period is a row of its own.</summary>
        public bool SplitByAPeriod => _spec.GroupBy.Any(c => c.Kind == "date" || QueryPeriods.IsYear(c) || QueryPeriods.IsYearMonth(c)
            || QueryPeriods.IsYearQuarter(c) || QueryPeriods.IsQuarter(c) || QueryPeriods.IsMonthOfYear(c));

        private static bool IsPeriodColumn(QueryColumn c) => c.Kind == "date" || QueryPeriods.IsYear(c) || QueryPeriods.IsYearMonth(c)
            || QueryPeriods.IsYearQuarter(c) || QueryPeriods.IsQuarter(c) || QueryPeriods.IsMonthOfYear(c);

        /// <summary>The query is split by a period, and a period the question compares with may be missing from its rows: some
        /// filter narrows a period column that is not split by, or keeps one value of the one that is (the months of one year, not
        /// the year before). With none, every period the data holds is a row of its own.</summary>
        public bool PeriodsNarrowedToOne => _spec.Filters.Any(f => IsPeriodColumn(f.Column)
            && (!_spec.GroupBy.Contains(f.Column) || (f.Op == QueryOps.Is && f.Values.Count == 1)));

        /// <summary>The periods a change is read between are all in the rows: two or more periods named, every period split by, or
        /// a split by a period that no filter narrows to one slice. Provable from the reading alone.</summary>
        public bool ChangeInRows => (SplitByAPeriod && !PeriodsNarrowedToOne)
            || (_reading != null && (_reading.Periods.Any(p => p.Span == "split") || _reading.Periods.Count(p => p.Span != "split") >= 2));

        /// <summary>The query shows two or more values of one column as rows of their own, and nothing else narrows it but a period: the
        /// values compared ARE the ones side by side. Another narrowing beside them ("Pinecrest vs kitchen") is not provably what is compared.</summary>
        public bool ShowsValuesSideBySide => _reading != null && _reading.SideBySide
            && _spec.Filters.Any(f => f.Op == QueryOps.Is && f.Values.Count >= 2 && _spec.GroupBy.Contains(f.Column))
            && _spec.Filters.All(f => (f.Op == QueryOps.Is && f.Values.Count >= 2 && _spec.GroupBy.Contains(f.Column)) || IsPeriodColumn(f.Column));

        /// <summary>The engine itself read the span from the question's words ("last year", "this quarter"), so the span shown IS
        /// the span asked. A filter on a period column alone is not that: "the 2024 holiday season" is a Year filter too.</summary>
        public bool SpanReadFromTheWords => _reading != null && _reading.Periods.Any(p => p.Span != "split");

        private static readonly System.Text.RegularExpressions.Regex Split = new System.Text.RegularExpressions.Regex(@"(?<=[a-z])(?=[A-Z])|[^A-Za-z]+");

        /// <summary>The whole words of a measure's own name, lower case ("Return Rate %" is return, rate and %; "NetSales" is net and
        /// sales). A description is never read.</summary>
        internal static List<string> NameTokens(string name) =>
            Split.Split(name ?? "").Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).Concat((name ?? "").Contains('%') ? new[] { "%" } : new string[0]).ToList();

        /// <summary>The kinds of number a question's words can name that a measure's OWN NAME can carry as a whole word. A kind
        /// clears "another kind of number" only when a measure the query shows is named for it (and a gross or net the words carry
        /// is in the name too). A count is not here on purpose: a count of the wrong thing is a wrong number. A description is
        /// never read: "generated" holds "rate" and "as per the finance team" holds "per".</summary>
        private static readonly (string Kind, System.Text.RegularExpressions.Regex Words, HashSet<string> Name)[] Kinds =
        {
            ("average", Rx(@"\b(avg|average|averages|mean)\b"), new HashSet<string> { "avg", "average", "mean", "per" }),
            ("profit", Rx(@"\bprofits?\b"), new HashSet<string> { "profit", "profits" }),
            ("margin", Rx(@"\bmargins?\b"), new HashSet<string> { "margin", "margins" }),
            ("price", Rx(@"\bpric(e|es|ing)\b"), new HashSet<string> { "price", "prices", "pricing" }),
            ("cost", Rx(@"\bcosts?\b"), new HashSet<string> { "cost", "costs" }),
            ("rate", Rx(@"\b(rate|rates|ratio|percent|percentage|share|proportion)\b"), new HashSet<string> { "rate", "rates", "ratio", "percent", "pct", "share", "proportion", "%" }),
        };
        private static System.Text.RegularExpressions.Regex Rx(string p) => new System.Text.RegularExpressions.Regex(p, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>The kind of number the words name that a measure the query shows is also named for, or null.</summary>
        private string KindShown(string words)
        {
            words ??= "";
            foreach (var (kind, w, names) in Kinds)
            {
                if (!w.IsMatch(words)) continue;
                foreach (var me in UsedMeasures)
                {
                    var tokens = NameTokens(me.Name);
                    if (!tokens.Any(names.Contains)) continue;
                    // "gross profit" is not "net profit": a qualifier the words carry must be in the measure's name.
                    var qualifier = System.Text.RegularExpressions.Regex.Match(words, @"\b(gross|net)\s+(?:" + kind + @")", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (qualifier.Success && !tokens.Contains(qualifier.Groups[1].Value.ToLowerInvariant())) continue;
                    return kind;
                }
            }
            return null;
        }

        // ---- what a ratio divides, held against the question's words ----------------------------------------------------

        private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.Ordinal)
        {
            "net", "gross", "amount", "value", "total", "of", "the", "count", "distinct", "rows", "number", "and", "a", "an", "model", "s", "data",
        };
        private static readonly HashSet<string> RateWords = new HashSet<string>(StringComparer.Ordinal) { "rate", "ratio", "percent", "percentage", "pct", "share", "proportion", "%" };

        private static string Stem(string w) => w.Length > 3 && w.EndsWith("s", StringComparison.Ordinal) ? w.Substring(0, w.Length - 1) : w;

        private static HashSet<string> Stems(IEnumerable<string> words) => new HashSet<string>(words.Select(Stem), StringComparer.Ordinal);

        /// <summary>The question names this ratio, or what it divides: every word of the measure's own name is in the words ("the
        /// return rate"), or a word of what it divides by (its denominator, said in the question: "of sales"). A share of
        /// ORDERS is not answered by a ratio of returned VALUE over sales VALUE.</summary>
        internal static bool RatioCovers(QueryMeasure ratio, string words)
        {
            var asked = Stems(NameTokens((words ?? "").Replace("%", " % ")));
            var name = NameTokens(ratio.Name).Where(w => w != "%" && !Generic.Contains(w)).ToList();
            // "return %" is the model's "Return Rate %": the words for a rate (rate, ratio, percent, %) stand for one another.
            if (name.Count > 0 && name.All(w => asked.Contains(Stem(w)) || (RateWords.Contains(w) && RateWords.Any(r => asked.Contains(Stem(r)))))) return true;
            var divides = ratio.RatioParts?.Denominator;
            var parts = NameTokens(divides).Where(w => !Generic.Contains(w) && !RateWords.Contains(w)).Select(Stem).ToList();
            return parts.Any(asked.Contains);
        }

        private static readonly HashSet<string> BaseFiller = new HashSet<string>(StringComparer.Ordinal) { "the", "a", "an", "our", "all", "my", "their", "its", "total", "s" };

        /// <summary>The ratio divides by what the words say the share is of: every word of that base, stemmed, is a word of the
        /// ratio's denominator ("sales" and "net sales" fit Net Sales; "gross sales" does not, "gross" and "net" count here).</summary>
        internal static bool BaseIs(QueryMeasure ratio, string of)
        {
            var divides = Stems(NameTokens(ratio.RatioParts?.Denominator));
            var asked = NameTokens(of).Where(w => !BaseFiller.Contains(w)).Select(Stem).ToList();
            return asked.Count > 0 && asked.All(divides.Contains);
        }

        private static readonly System.Text.RegularExpressions.Regex PartOf = Rx(
            @"\b(?:days?|weeks?|weekends?|months?|quarters?|half|halves|seasons?|periods?|part|parts|start|beginning|end|middle|holidays?)\s+of\s+(?:the\s+)?$");

        /// <summary>The words name a span INSIDE the period the engine read: "last year's holiday season", "the first quarter of this
        /// year", "the first two weeks of last month". Then the whole period stated is not the span asked, and a period gap is not
        /// covered by it (verifier round 5, R10).</summary>
        private bool SpanInsideThePeriod(string words)
        {
            if (_reading == null || string.IsNullOrEmpty(words)) return false;
            foreach (var p in _reading.Periods.Where(p => p.Span != "split" && !string.IsNullOrWhiteSpace(p.Words)))
            {
                foreach (System.Text.RegularExpressions.Match m in Rx(@"\b" + System.Text.RegularExpressions.Regex.Escape(p.Words.Trim()) + @"\b").Matches(words))
                {
                    var after = words.Substring(m.Index + m.Length);
                    if (after.StartsWith("'s", StringComparison.Ordinal) || after.StartsWith("\u2019s", StringComparison.Ordinal)) return true;
                    if (PartOf.IsMatch(words.Substring(0, m.Index))) return true;
                }
            }
            return false;
        }

        /// <summary>The ratio measures the query shows.</summary>
        public IEnumerable<QueryMeasure> Ratios => UsedMeasures.Where(m => m.IsRatio);

        /// <summary>Whether the reading itself covers a gap the check named, from its own facts and nothing else. Null when it
        /// does not; else the sentence the answer says so with ("" when the statement already says it). Only what the reading
        /// PROVABLY covers clears anything: no fact clears an absence, a forecast, an unnamed thing, a narrowing, a ranking or a
        /// split, and the check never asks this of a gap Jev is sure of (JevQueryCheck.First).</summary>
        public string Clears(string gap, string words)
        {
            switch (gap)
            {
                case "change":
                    if (ChangeInRows) return "You asked how it changed. The periods are shown side by side, so read the change off the rows.";
                    if (ShowsValuesSideBySide) return "You asked how it changed. The two are shown side by side, so read the difference off the rows.";
                    return null;
                case "versions":
                    // Two measures are two versions only when nothing but a period narrows them: with "is not Kitchen" beside them, both
                    // numbers are the without-kitchen version, and the with-kitchen version asked for is in no row (verifier round 5, R11).
                    if (ShowsValuesSideBySide || (_spec.Measures.Count >= 2 && _spec.Filters.All(f => IsPeriodColumn(f.Column))))
                        return "You asked for two versions. They are shown side by side, so read the difference off the rows.";
                    return null;
                case "share":
                    // A share the words say is OF something ("as a share of gross sales") is covered only by a ratio that divides by
                    // that: the ratio's own name in the words does not make its base the one asked (verifier round 5, R13).
                    var of = QueryGuards.ShareOf(words);
                    if (of != null && !Ratios.Any(m => BaseIs(m, of))) return null;
                    var ratio = Ratios.FirstOrDefault(m => RatioCovers(m, words) && (of == null || BaseIs(m, of)));
                    if (ratio != null) return ratio.Name + " is itself a ratio (" + ratio.RatioParts.Value.Numerator + " divided by " + ratio.RatioParts.Value.Denominator + "), so the number shown is the share, rate or average asked for.";
                    var shown = KindShown(words);
                    return shown == "average" || shown == "rate" ? "The measure shown is the " + shown + " asked for." : null;
                case "other_number":
                    var kind = KindShown(words);
                    return kind == null ? null : "The measure shown is the " + kind + " asked for.";
                case "period":
                    return SpanReadFromTheWords && !SpanInsideThePeriod(words) ? "" : null;
                default:
                    return null;
            }
        }

        // ---- for the person --------------------------------------------------------------------------------------

        /// <summary>"This shows Net Sales for each Region, where Year is 2025." A sentence, ending in a full stop.</summary>
        public string Person
        {
            get
            {
                var sb = new System.Text.StringBuilder("This shows ");
                sb.Append(Join(_spec.Measures.Select(Shows).ToList()));
                sb.Append(_spec.GroupBy.Count == 0 ? " as one total" : " for each " + Join(_spec.GroupBy.Select(ShownSplit).ToList()));
                var narrowing = _spec.Filters.GroupBy(f => f.Column).Select(g => PersonNarrowing(g.Key, g.ToList())).ToList();
                if (narrowing.Count > 0) sb.Append(", where ").Append(Join(narrowing));
                if (_spec.Top.HasValue && _spec.Top.Value > 0)
                    sb.Append(", keeping the ").Append(_spec.Lowest ? "bottom " : "top ").Append(_spec.Top.Value.ToString("N0", CultureInfo.InvariantCulture))
                      .Append(" by ").Append((_spec.TopBy ?? _spec.Measures[0]).Name);
                else if (_spec.SortMeasure != null && _spec.Measures.Contains(_spec.SortMeasure))
                    sb.Append(", with the ").Append(_spec.Descending ? "highest " : "lowest ").Append(_spec.SortMeasure.Name).Append(" first");
                else if (_spec.SortColumn != null && _spec.GroupBy.Contains(_spec.SortColumn))
                    sb.Append(", with ").Append(_cat.Shown(_spec.SortColumn)).Append(' ').Append(QueryTemplate.SortWords(_spec.SortColumn, _spec.Descending));
                sb.Append('.');
                foreach (var side in SideBySide())
                    sb.Append(' ').Append(side.Person);
                foreach (var p in _reading?.Periods.Where(p => p.Span == "split") ?? Enumerable.Empty<JevQueryReader.PeriodFact>())
                    sb.Append(" Every ").Append(p.Unit).Append(" is shown side by side, so you can read off the one you meant.");
                if (_reading != null && _reading.TopOverPairs && _spec.GroupBy.Count >= 2)
                    sb.Append(" The ").Append(_spec.Lowest ? "bottom " : "top ").Append(_spec.Top.GetValueOrDefault().ToString(CultureInfo.InvariantCulture))
                      .Append(" is taken over every ").Append(_cat.Shown(_spec.GroupBy[0])).Append(" and ").Append(_cat.Shown(_spec.GroupBy[1])).Append(" pair together.");
                return sb.ToString();
            }
        }

        /// <summary>A measure as the person reads it: a ratio says what it divides ("Return Rate % (Returns Amount divided by Net
        /// Sales Amount)"), so "of orders" is never read into a ratio of value.</summary>
        private static string Shows(QueryMeasure m) => m.RatioParts is { } p ? m.Name + " (" + p.Numerator + " divided by " + p.Denominator + ")" : m.Name;

        private string PersonNarrowing(QueryColumn column, List<QueryFilter> filters)
        {
            string V(QueryFilter f, int i) => ShownValue(column, f.Values[i]);
            var parts = new List<string>();
            foreach (var f in filters)
                switch (f.Op)
                {
                    case QueryOps.Is: parts.Add("is " + string.Join(" or ", f.Values.Select(v => ShownValue(column, v)))); break;
                    case QueryOps.IsNot: parts.Add("is not " + string.Join(" or ", f.Values.Select(v => ShownValue(column, v)))); break;
                    case QueryOps.After: parts.Add("is after " + V(f, 0)); break;
                    case QueryOps.From: parts.Add("is " + V(f, 0) + " or later"); break;
                    case QueryOps.Before: parts.Add("is before " + V(f, 0)); break;
                    case QueryOps.UpTo: parts.Add("is " + V(f, 0) + " or earlier"); break;
                }
            // One column, one "is": "Date is 1 January 2024 or later and before 12 October 2024".
            var text = new System.Text.StringBuilder(_cat.Shown(column));
            for (var i = 0; i < parts.Count; i++)
                text.Append(i == 0 ? " " : " and ").Append(i == 0 ? parts[i] : parts[i].StartsWith("is ", StringComparison.Ordinal) ? parts[i].Substring(3) : parts[i]);
            // Every calendar period is said in calendar dates (round 7), so a person who asked for another kind of year sees the gap here;
            // a period of another kind (Fiscal Quarter) says no dates, since the engine cannot know when it starts (PeriodDates.Calendar).
            var dates = PeriodDates.Of(column, filters, _cat);
            if (dates != null) text.Append(" (").Append(dates).Append(')');
            return text.ToString();
        }

        /// <summary>A split column as the person reads it; a period says what one row of it is.</summary>
        private string ShownSplit(QueryColumn column)
        {
            var row = PeriodDates.Row(column, _cat);
            return row == null ? _cat.Shown(column) : _cat.Shown(column) + " (" + row + ")";
        }

        // ---- for Jev ---------------------------------------------------------------------------------------------

        /// <summary>The reading told in the question's own words. Names of the model's own measures and columns go (they are
        /// sent in model_fields as well); values and dates never do.</summary>
        public string ForJev
        {
            get
            {
                var sentences = new List<string>
                {
                    "It shows " + Join(_spec.Measures.Select(m => m.Name).ToList()) + ".",
                    _spec.GroupBy.Count == 0 ? "It is one total, split by nothing." : "It is split by " + Join(_spec.GroupBy.Select(_cat.Shown).ToList()) + ", one row for each.",
                };
                // What the query itself shows, said before anything is asked: a measure that is a ratio, a split by a period.
                foreach (var m in Ratios)
                    sentences.Add(m.Name + " is a ratio, " + m.RatioParts.Value.Numerator + " divided by " + m.RatioParts.Value.Denominator
                        + ", so it is a share, a rate or an average of " + m.RatioParts.Value.Denominator + " and of nothing else.");
                if (SplitByAPeriod)
                    sentences.Add(PeriodsNarrowedToOne
                        ? "It is split by a period, so the rows hold only the periods named below, one row each, and no other period."
                        : "Because it is split by a period, every period is a row of its own and the person reads any change from one row to the next.");
                var told = 0;
                foreach (var g in _spec.Filters.GroupBy(f => f.Column))
                {
                    foreach (var f in g)
                    {
                        if (_reading != null && _reading.Periods.Any(p => p.Filters.Contains(f))) continue;
                        var words = WordsOf(f);
                        sentences.Add(words == null
                            ? "It is narrowed by " + _cat.Shown(g.Key) + "."
                            : "It is narrowed to rows where " + _cat.Shown(g.Key) + " " + Told(f.Op, words) + ".");
                        told++;
                    }
                }
                foreach (var p in _reading?.Periods ?? new List<JevQueryReader.PeriodFact>())
                {
                    sentences.Add(PeriodTold(p));
                    told++;
                }
                if (told == 0) sentences.Add("It is not narrowed by anything.");
                if (_spec.Top.HasValue && _spec.Top.Value > 0)
                    sentences.Add("It keeps only the " + (_spec.Lowest ? "bottom " : "top ") + _spec.Top.Value.ToString(CultureInfo.InvariantCulture)
                        + " rows, ranked by " + (_spec.TopBy ?? _spec.Measures[0]).Name + ".");
                else if (_spec.SortMeasure != null && _spec.Measures.Contains(_spec.SortMeasure))
                    sentences.Add("The rows are ordered by " + _spec.SortMeasure.Name + ", " + (_spec.Descending ? "largest first." : "smallest first."));
                else if (_spec.SortColumn != null && _spec.GroupBy.Contains(_spec.SortColumn))
                    sentences.Add("The rows are ordered by " + _cat.Shown(_spec.SortColumn) + ", " + QueryTemplate.SortWords(_spec.SortColumn, _spec.Descending) + ".");
                foreach (var side in SideBySide())
                    sentences.Add(side.Jev);
                foreach (var p in _reading?.Periods.Where(p => p.Span == "split") ?? Enumerable.Empty<JevQueryReader.PeriodFact>())
                    sentences.Add("The " + p.Unit + " words could not be pinned to one " + p.Unit + ", so every " + p.Unit + " is shown side by side and the person reads off the one they meant, and any change between them.");
                if (_reading != null && _reading.TopOverPairs && _spec.GroupBy.Count >= 2)
                    sentences.Add("The top rows are taken over every " + _cat.Shown(_spec.GroupBy[0]) + " and " + _cat.Shown(_spec.GroupBy[1]) + " pair together.");
                sentences.Add(ShowsOnly);
                return string.Join(" ", sentences);
            }
        }

        /// <summary>The words of the question a filter's values came from, quoted and joined, or null when none did. The
        /// question's own words: never the model's value.</summary>
        private string WordsOf(QueryFilter f)
        {
            var words = (_reading?.Filters ?? new List<(ValueCandidate Candidate, string Op)>())
                .Where(x => x.Candidate.Column == f.Column && x.Op == f.Op).Select(x => x.Candidate.Words).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return words.Count == 0 ? null : string.Join(" or ", words.Select(w => "'" + w + "'"));
        }

        private static string Told(string op, string words) => op switch
        {
            QueryOps.Is => "is " + words,
            QueryOps.IsNot => "is not " + words,
            QueryOps.After => "comes after " + words,
            QueryOps.From => "is " + words + " or later",
            QueryOps.Before => "comes before " + words,
            QueryOps.UpTo => "is " + words + " or earlier",
            _ => op + " " + words,
        };

        /// <summary>A period, told without a date or a label.</summary>
        private static string PeriodTold(JevQueryReader.PeriodFact p)
        {
            var unit = p.Unit;
            // The question's own period words, so the span told here is plainly the span they ask for ("last year" is the whole of the
            // year before the latest one): never a date, only the words the question held.
            var said = string.IsNullOrWhiteSpace(p.Words) ? "" : $"The question's words '{p.Words}' are read as ";
            switch (p.Span)
            {
                case "latest": return (said.Length > 0 ? said + $"the latest {unit} the data holds, the whole of it, so the period is stated." : $"It is narrowed to the latest {unit} the data holds, the whole of it.");
                case "before": return (said.Length > 0 ? said + $"the whole of the {unit} before the latest {unit} the data holds, so the period is stated." : $"It is narrowed to the whole of the {unit} before the latest {unit} the data holds.");
                case "todate-before": return (said.Length > 0 ? said + $"the span from the first day of the {unit} before the latest {unit} to the same day of that {unit} as the last day the data holds, so the period is stated." : $"It is narrowed to the span from the first day of the {unit} before the latest {unit} to the same day of that {unit} as the last day the data holds.");
                case "split": return $"The question's words '{p.Words}' name a {unit} the reading could not pin down.";
                default: return $"It is narrowed to a {unit} read from the words '{p.Words}'.";
            }
        }

        // ---- values shown side by side ---------------------------------------------------------------------------

        private IEnumerable<(string Person, string Jev)> SideBySide()
        {
            if (_reading == null || !_reading.SideBySide) yield break;
            // Two or more values of a column that is also split by: each is its own row.
            foreach (var f in _spec.Filters.Where(f => f.Op == QueryOps.Is && f.Values.Count >= 2 && _spec.GroupBy.Contains(f.Column)))
            {
                var words = (_reading.Filters).Where(x => x.Candidate.Column == f.Column && x.Op == QueryOps.Is).Select(x => x.Candidate.Words)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Select(w => "'" + w + "'").ToList();
                yield return (Join(f.Values.Select(v => ShownValue(f.Column, v)).ToList()) + " are shown side by side, one row each.",
                    words.Count >= 2 ? Join(words) + " are shown side by side, one row each, so the person reads the difference off the rows." : "The values are shown side by side, one row each, so the person reads the difference off the rows.");
            }
            if (_reading.Periods.Count(p => p.Span != "split") >= 2)
                yield return ("The periods are shown side by side.", "The periods the question names are shown side by side, one row each, so the person reads any change off the rows.");
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        private static string ShownValue(QueryColumn c, object v) =>
            v is DateTime d ? d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
            : c.Kind == "decimal" ? Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("0.##########", CultureInfo.InvariantCulture)
            : Convert.ToString(v, CultureInfo.InvariantCulture);

        private static string Join(IReadOnlyList<string> items) =>
            items.Count == 0 ? "" : items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];
    }
}
