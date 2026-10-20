// "How many customers bought twice": a count of customers by how many orders each one has (UAT 17 Oct, P21).
//
// WHY THE ENGINE READS THIS ITSELF. The one query shape (QueryTemplate) shows a measure split by columns. "Bought twice" is not a
// measure: it is a question ABOUT a measure, asked per customer, and the shape cannot say it. Jev picked the nearest part it was
// offered (Order Count as one total) and the page showed 25,669 as if it answered. The words are plain, so the engine reads them:
// the entity (a table with a key), the counted thing (an order count measure that table filters), and the test (exactly 2, 2 or
// more, more than 2). It states the assumption ("exactly 2 orders") and offers the other reading one press away.
//
// It reads ONLY what it is sure of. Anything with another part in it (a split, a period, a place) returns null and goes to Jev
// as before, where the check now keeps a flagged reading from being shown as the answer.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Query
{
    internal static class FrequencyReading
    {
        internal const string Equal = "=", AtLeast = ">=", MoreThan = ">";

        internal sealed class Read
        {
            public QueryColumn Entity;
            /// <summary>What counts an order: an order-count measure the entity's table filters, else (a model with none) the column that
            /// names the order, counted by its distinct values. Exactly one is set.</summary>
            public QueryMeasure Counter;
            public QueryColumn CounterColumn;
            public string CounterName => Counter?.Name ?? (CounterColumn == null ? null : CounterColumn.Table + " " + CounterColumn.Name);
            public string CounterDax => Counter != null ? Counter.Dax : "DISTINCTCOUNT(" + CounterColumn.Dax + ")";
            public string Op;
            public int N;
            /// <summary>The year the question names ("in 2025"), with the column that holds it; both null when none is named.</summary>
            public int? Year;
            public QueryColumn YearColumn;
            /// <summary>The entity said as a plural ("Customers").</summary>
            public string Plural => Plural_(PlainName(Entity.Table));
            /// <summary>The test said in a few words: "exactly 2 orders", "2 or more orders", "more than 2 orders".</summary>
            public string Phrase => Say(Op, N) + YearWords;
            /// <summary>" in 2025", or nothing.</summary>
            public string YearWords => Year != null ? " in " + Year.Value.ToString(CultureInfo.InvariantCulture) : "";
        }

        /// <summary>A table as people say it: "dim_customer" is "customer".</summary>
        internal static string PlainName(string table) =>
            Regex.Replace(Regex.Replace(table, @"^(?:dim|fact|d|f)[_ ]", "", RegexOptions.IgnoreCase), "[_ ]+", " ").Trim();

        internal static string Plural_(string table) =>
            table.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? table
            : table.EndsWith("y", StringComparison.OrdinalIgnoreCase) && table.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(table[^2])) ? table.Substring(0, table.Length - 1) + "ies"
            : table + "s";

        internal static string Say(string op, int n)
        {
            var orders = n == 1 ? "order" : "orders";
            return op == AtLeast ? $"{n} or more orders" : op == MoreThan ? $"more than {n} {orders}" : $"exactly {n} {orders}";
        }

        /// <summary>The question that asks the same thing in the other test, in words this reader reads back (the "or more" press).</summary>
        internal static string QuestionFor(Read r, string op) =>
            $"how many {r.Plural.ToLowerInvariant()} bought " + (op == AtLeast ? $"{r.N} or more times" : op == MoreThan ? $"more than {r.N} times" : $"exactly {r.N} times")
            + (r.Year != null ? " in " + r.Year.Value.ToString(CultureInfo.InvariantCulture) : "");

        private static readonly string[] Vocabulary = { "customers", "customer", "bought", "purchased", "ordered", "twice", "thrice", "times" };

        // "then" for "than" is the commonest slip in this shape, and at four letters it is too short for the general rule.
        private static readonly string[] ThanSlips = { "then", "thn", "thna", "tahn" };

        private static readonly Dictionary<string, int> Words = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["once"] = 1, ["one"] = 1, ["twice"] = 2, ["two"] = 2, ["thrice"] = 3, ["three"] = 3, ["four"] = 4, ["five"] = 5,
            ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        };

        private const string Num = @"\d+|once|one|twice|two|thrice|three|four|five|six|seven|eight|nine|ten";
        private static readonly Regex MoreThanRx = new Regex($@"\b(?:more than|over|greater than)\s+(?<n>{Num})(?:\s+times?)?\b", RegexOptions.Compiled);
        private static readonly Regex AtLeastRx = new Regex($@"\b(?:at least\s+(?<n>{Num})(?:\s+times?)?|(?<n>{Num})\s+or more(?:\s+times?)?|(?<n>{Num})\+\s*times?|(?<n>twice|once|thrice) or more)\b", RegexOptions.Compiled);
        private static readonly Regex EqualRx = new Regex($@"\b(?:exactly\s+)?(?<n>{Num})\s+times?\b|\b(?<n>once|twice|thrice)\b", RegexOptions.Compiled);
        private static readonly Regex VerbRx = new Regex(@"\b(?:bought|purchased|ordered|shopped|placed)\b", RegexOptions.Compiled);
        // "in 2025": the one narrowing this reading carries, a year, applied to the orders each customer is counted by.
        private static readonly Regex YearRx = new Regex(@"\b(?:in|during|for|of|within)\s+((?:19|20)\d\d)\b", RegexOptions.Compiled);
        private static readonly Regex CountRx = new Regex(@"\b(?:how many|number of|count of|count)\b", RegexOptions.Compiled);

        /// <summary>The reading, or null when the question is not this shape or the model does not hold what it needs.</summary>
        public static Read TryRead(string question, QueryCatalog cat)
        {
            if (string.IsNullOrWhiteSpace(question) || cat == null) return null;
            var tokens = Regex.Matches(question.ToLowerInvariant(), @"[a-z]+|\d+\+?").Cast<Match>().Select(m => m.Value).ToList();
            // Slips are forgiven for the words this shape is made of ("custmers", "twise"); nothing else is touched.
            for (var i = 0; i < tokens.Count; i++) tokens[i] = Forgive(tokens[i]);
            var tableOf = TablesNamed(tokens, cat);
            var text = string.Join(" ", tokens.Select(t => tableOf.ContainsKey(t) ? "customers" : t));
            if (!CountRx.IsMatch(text) || !VerbRx.IsMatch(text)) return null;

            int? year = null;
            var ym = YearRx.Match(text);
            if (ym.Success) { year = int.Parse(ym.Groups[1].Value, CultureInfo.InvariantCulture); text = text.Remove(ym.Index, ym.Length); }
            string op; int n;
            if (!TryTest(text, out op, out n, out var rest)) return null;
            if (n < 1) return null;
            // What is left of the question must be nothing but the shape's own words and the entity.
            var left = Regex.Replace(rest, @"\b(?:how many|number of|count of|count|customers|bought|purchased|ordered|shopped|placed|have|has|did|do|who|that|which|the|a|of|are|were|is|us|our|exactly|at least|times?|more|than)\b", " ");
            // Anything else the question asks for (a split, a place, a period) is not this reading's to drop: it goes to Jev.
            if (Regex.IsMatch(left, @"[a-z0-9]")) return null;

            var entityTable = tableOf.Values.Distinct().Take(2).ToList();
            if (entityTable.Count != 1) return null;
            var key = EntityKey(entityTable[0], cat);
            if (key == null) return null;
            var counter = CounterFor(entityTable[0], cat);
            var counterColumn = counter == null ? OrderColumnFor(entityTable[0], cat) : null;
            if (counter == null && counterColumn == null) return null;
            QueryColumn yearColumn = null;
            if (year != null)
            {
                yearColumn = YearColumnFor(cat, counter, counterColumn);
                if (yearColumn == null) return null;   // a year the model cannot filter by is left to Jev, who says so
            }
            return new Read { Entity = key, Counter = counter, CounterColumn = counterColumn, Op = op, N = n, Year = year, YearColumn = yearColumn };
        }

        private static bool TryTest(string text, out string op, out int n, out string rest)
        {
            foreach (var (rx, kind) in new[] { (MoreThanRx, MoreThan), (AtLeastRx, AtLeast), (EqualRx, Equal) })
            {
                var m = rx.Match(text);
                if (!m.Success) continue;
                var g = m.Groups["n"].Captures.Cast<Capture>().Select(c => c.Value).FirstOrDefault(v => v.Length > 0) ?? m.Groups["n"].Value;
                g = g.TrimEnd('+');
                n = Words.TryGetValue(g, out var w) ? w : int.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 0;
                op = kind;
                rest = text.Remove(m.Index, m.Length);
                return true;
            }
            op = null; n = 0; rest = text;
            return false;
        }

        private static string Forgive(string token)
        {
            if (ThanSlips.Contains(token)) return "than";
            if (token.Length < 5 || Vocabulary.Contains(token)) return token;
            foreach (var word in Vocabulary)
                if (Math.Abs(word.Length - token.Length) <= 1 && Distance(word, token) <= (word.Length >= 7 ? 2 : 1)) return word;
            return token;
        }

        /// <summary>The edit distance where two letters swapped ("bougth") count as one slip.</summary>
        internal static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
                for (var j = 1; j <= b.Length; j++)
                {
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }

        /// <summary>The tables a word of the question names (its own name, its plural, or a synonym), by the word.</summary>
        internal static Dictionary<string, string> TablesNamed(List<string> tokens, QueryCatalog cat)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var tables = cat.Columns.Where(c => c.Key).Select(c => c.Table).Distinct(StringComparer.Ordinal).ToList();
            foreach (var t in tables)
            {
                // "dim_customer" is the customers table: the warehouse prefix and the underscores are not part of what people call it.
                var plain = PlainName(t);
                var names = new List<string> { t.ToLowerInvariant(), Plural_(t).ToLowerInvariant(), plain.ToLowerInvariant(), Plural_(plain).ToLowerInvariant() };
                if (cat.TableSynonyms.TryGetValue(t, out var syn)) names.AddRange(syn.Select(s => s.ToLowerInvariant()));
                foreach (var tok in tokens)
                    if (names.Contains(tok) || (tok.Length >= 6 && names.Any(nm => nm.Length >= 6 && Distance(nm, tok) <= 1))) found[tok] = t;
            }
            return found;
        }

        /// <summary>The table's key a count is taken over: the key column other tables point at, named for the table when there are several.</summary>
        internal static QueryColumn EntityKey(string table, QueryCatalog cat)
        {
            var keys = cat.Columns.Where(c => c.Key && string.Equals(c.Table, table, StringComparison.Ordinal)).ToList();
            if (keys.Count == 0) return null;
            // A table that is only ever the many side (a fact table) is not an entity to count.
            if (!cat.Edges.Any(e => string.Equals(e.To, table, StringComparison.Ordinal))) return null;
            return keys.FirstOrDefault(c => c.Name.EndsWith("Key", StringComparison.OrdinalIgnoreCase) || c.Name.EndsWith("ID", StringComparison.OrdinalIgnoreCase)) ?? keys[0];
        }

        /// <summary>The measure that counts orders for the entity: an order count the entity's table filters.</summary>
        internal static QueryMeasure CounterFor(string table, QueryCatalog cat)
        {
            return cat.Measures
                .Where(m => Regex.IsMatch(m.Name, "order", RegexOptions.IgnoreCase) && !NotSalesRx.IsMatch(m.Name)
                            && Regex.IsMatch(m.Expression ?? "", @"\b(?:DISTINCTCOUNT|COUNTROWS|COUNT|DISTINCTCOUNTNOBLANK)\b", RegexOptions.IgnoreCase)
                            && cat.Unreached(table, m).Length == 0)
                .OrderBy(m => m.Hidden ? 1 : 0)
                .ThenBy(m => Regex.IsMatch(m.Name, "count|number|orders", RegexOptions.IgnoreCase) ? 0 : 1)
                .ThenBy(m => m.Name.Length)
                .FirstOrDefault();
        }

        internal static string YearFilter(Read r) =>
            r.Year == null ? "" : ", " + r.YearColumn.Dax + " = " + (r.YearColumn.Kind == "text" ? "\"" + r.Year.Value.ToString(CultureInfo.InvariantCulture) + "\"" : r.Year.Value.ToString(CultureInfo.InvariantCulture));

        /// <summary>The model's year column that the counted orders can be filtered by (the date table's first).</summary>
        internal static QueryColumn YearColumnFor(QueryCatalog cat, QueryMeasure counter, QueryColumn counterColumn) =>
            cat.Visible.Where(c => Regex.IsMatch(c.Name, "^year$", RegexOptions.IgnoreCase) && (c.Kind == "whole" || c.Kind == "text")
                                   && (counter != null ? cat.Unreached(c.Table, counter).Length == 0 : cat.Filters(c.Table, counterColumn.Table)))
                .OrderBy(c => cat.DateTables.Contains(c.Table) ? 0 : 1).FirstOrDefault();

        private static readonly Regex NotSalesRx = new Regex("return|refund|cancel|void|credit|reject", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SalesRx = new Regex("sale|order|transaction|invoice|purchase", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A model with no order-count measure: the column that names the order on the table of sales the entity's table filters
        /// ("fact_sales[order_number]"). A table of returns is never the one (the first version counted the returns table's orders: a wrong number),
        /// and when two tables could be it and the names do not say which, nothing is read here: Jev reads it, and the check judges the reading.</summary>
        internal static QueryColumn OrderColumnFor(string table, QueryCatalog cat) =>
            OrderColumnWhere(cat, c => cat.Filters(table, c.Table) && !string.Equals(c.Table, table, StringComparison.Ordinal));

        /// <summary>"How many orders" on a model with no order-count measure and no table of orders: the same column, wherever it sits.</summary>
        internal static QueryColumn OrderColumnAny(QueryCatalog cat) => OrderColumnWhere(cat, _ => true);

        private static QueryColumn OrderColumnWhere(QueryCatalog cat, Func<QueryColumn, bool> reaches)
        {
            var candidates = cat.Columns.Where(c => !c.Hidden && Regex.IsMatch(c.Name, @"^orders?[ _]?(?:number|no|id|code|key)$|^(?:number|no|id)[ _]?of[ _]?orders?$", RegexOptions.IgnoreCase)
                                                    && reaches(c) && !NotSalesRx.IsMatch(c.Table)).ToList();
            var tables = candidates.Select(c => c.Table).Distinct(StringComparer.Ordinal).ToList();
            if (tables.Count > 1)
            {
                var named = tables.Where(t => SalesRx.IsMatch(t)).ToList();
                if (named.Count != 1) return null;
                candidates = candidates.Where(c => c.Table == named[0]).ToList();
            }
            return candidates.OrderBy(c => c.Key ? 1 : 0).FirstOrDefault();
        }

        /// <summary>The query: how many of the entity's rows have an order count that passes the test. Plus zero, so "none" reads 0, not blank.</summary>
        public static string Dax(Read r)
        {
            var key = r.Entity.Dax;
            var test = r.Op == AtLeast ? ">=" : r.Op == MoreThan ? ">" : "=";
            return "EVALUATE\nROW(\n    \"" + r.Plural.Replace("\"", "\"\"") + "\", COUNTROWS(FILTER(ADDCOLUMNS(VALUES(" + key + "), \"@Orders\", CALCULATE(" + r.CounterDax + YearFilter(r) + ")), [@Orders] " + test + " " + r.N.ToString(CultureInfo.InvariantCulture) + ")) + 0\n)";
        }
    }
}
