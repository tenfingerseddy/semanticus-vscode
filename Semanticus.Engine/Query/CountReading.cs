// "How many customers / stores / products / different customers / orders did we sell in 2023": a plain count of a THING. The one query
// shape shows a measure split by columns, so Jev picked the nearest number it was offered (a sum of lines, an order total) and the
// check had to flag it (UAT 17 Oct). The words are plain, so the engine counts the thing itself, the way the order-count reader does:
// the table's key counted once each, or, when the words say the thing was SOLD or a period is named, only the rows the sales reach.
//
// It reads ONLY what it is sure of. Any other part (a split, a place, a fiscal year, a value) returns null and goes to Jev as before.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Query
{
    internal static class CountReading
    {
        internal sealed class Read
        {
            /// <summary>The table's key counted; null when the thing is orders, counted by the order measure or column.</summary>
            public QueryColumn Entity;
            public QueryMeasure Counter;
            public QueryColumn CounterColumn;
            public int? Year;
            public QueryColumn YearColumn;
            /// <summary>True when only the things the sales reach count (sold, or a period named); false counts every row of the table.</summary>
            public bool Active;
            public string Plural;
            public string CounterName => Counter?.Name ?? (CounterColumn == null ? null : CounterColumn.Table + " " + CounterColumn.Name);
            public string YearWords => Year != null ? " in " + Year.Value.ToString(CultureInfo.InvariantCulture) : "";
        }

        private static readonly string[] Vocabulary = { "customers", "products", "stores", "orders", "different", "distinct", "unique", "number", "many" };
        private static readonly Regex CountRx = new Regex(@"\b(?:how many|number of|count of|count)\b", RegexOptions.Compiled);
        private static readonly Regex YearRx = new Regex(@"\b(?:in|during|for|of|within)\s+((?:19|20)\d\d)\b", RegexOptions.Compiled);
        private static readonly Regex ActiveRx = new Regex(@"\b(?:sell|sold|bought|buy|ordered|purchased|shopped|placed)\b", RegexOptions.Compiled);
        private static readonly Regex ShapeWords = new Regex(
            @"\b(?:how many|number of|count of|count|different|distinct|unique|do|does|did|we|have|has|our|the|my|all|sell|sold|bought|buy|ordered|purchased|shopped|placed|were|was|are|is|there|in|during|of|a|to|from|us|total|customers|products|stores|orders)\b", RegexOptions.Compiled);

        public static Read TryRead(string question, QueryCatalog cat)
        {
            if (string.IsNullOrWhiteSpace(question) || cat == null) return null;
            var tokens = Regex.Matches(question.ToLowerInvariant(), @"[a-z]+|\d+").Cast<Match>().Select(m => m.Value).ToList();
            for (var i = 0; i < tokens.Count; i++) tokens[i] = Forgive(tokens[i]);
            var tableOf = FrequencyReading.TablesNamed(tokens, cat);
            var text = string.Join(" ", tokens.Select(t => tableOf.ContainsKey(t) ? "customers" : t));
            if (!CountRx.IsMatch(text)) return null;
            int? year = null;
            var ym = YearRx.Match(text);
            if (ym.Success) { year = int.Parse(ym.Groups[1].Value, CultureInfo.InvariantCulture); text = text.Remove(ym.Index, ym.Length); }
            // Anything else the question asks for (a split, a place, a period word, a value) is not this reading's to drop.
            if (Regex.IsMatch(ShapeWords.Replace(text, " "), @"[a-z0-9]")) return null;
            var active = year != null || ActiveRx.IsMatch(text);

            var tables = tableOf.Values.Distinct().Take(2).ToList();
            if (tables.Count > 1) return null;
            if (tables.Count == 1)
            {
                var key = FrequencyReading.EntityKey(tables[0], cat);
                if (key == null) return null;
                var plural = Cap(FrequencyReading.Plural_(FrequencyReading.PlainName(tables[0])));
                if (!active) return new Read { Entity = key, Plural = plural };
                var counter = FrequencyReading.CounterFor(tables[0], cat);
                var column = counter == null ? FrequencyReading.OrderColumnFor(tables[0], cat) : null;
                if (counter == null && column == null) return null;
                QueryColumn yearColumn = null;
                if (year != null)
                {
                    yearColumn = FrequencyReading.YearColumnFor(cat, counter, column);
                    if (yearColumn == null) return null;
                }
                return new Read { Entity = key, Counter = counter, CounterColumn = column, Year = year, YearColumn = yearColumn, Active = true, Plural = plural };
            }
            // "orders" with no table of that name: the order-count measure counts them.
            if (!Regex.IsMatch(text, @"\borders\b")) return null;
            var measure = cat.Measures
                .Where(m => Regex.IsMatch(m.Name, "order", RegexOptions.IgnoreCase) && !Regex.IsMatch(m.Name, "return|refund|cancel|void|credit|reject", RegexOptions.IgnoreCase)
                            && Regex.IsMatch(m.Expression ?? "", @"\b(?:DISTINCTCOUNT|COUNTROWS|COUNT|DISTINCTCOUNTNOBLANK)\b", RegexOptions.IgnoreCase)
                            && !MeasureTime.Moves(m, cat))
                .OrderBy(m => m.Hidden ? 1 : 0).ThenBy(m => Regex.IsMatch(m.Name, "count|number|orders", RegexOptions.IgnoreCase) ? 0 : 1).ThenBy(m => m.Name.Length)
                .FirstOrDefault();
            // No order-count measure: the order key is counted once each (Contoso: fact_sales[order_number]).
            var orderKey = measure == null ? FrequencyReading.OrderColumnAny(cat) : null;
            if (measure == null && orderKey == null) return null;
            QueryColumn yc = null;
            if (year != null) { yc = FrequencyReading.YearColumnFor(cat, measure, orderKey); if (yc == null) return null; }
            return new Read { Counter = measure, CounterColumn = orderKey, Year = year, YearColumn = yc, Active = true, Plural = "Orders" };
        }

        public static string Dax(Read r)
        {
            var name = r.Plural.Replace("\"", "\"\"");
            var year = r.YearColumn == null ? "" : ", " + r.YearColumn.Dax + " = " + (r.YearColumn.Kind == "text" ? "\"" + r.Year.Value.ToString(CultureInfo.InvariantCulture) + "\"" : r.Year.Value.ToString(CultureInfo.InvariantCulture));
            var counted = r.Counter != null ? r.Counter.Dax : "DISTINCTCOUNT(" + r.CounterColumn?.Dax + ")";
            if (r.Entity == null) return "EVALUATE\nROW(\n    \"" + name + "\", CALCULATE(" + counted + year + ")\n)";
            if (!r.Active) return "EVALUATE\nROW(\n    \"" + name + "\", DISTINCTCOUNT(" + r.Entity.Dax + ")\n)";
            return "EVALUATE\nROW(\n    \"" + name + "\", COUNTROWS(FILTER(VALUES(" + r.Entity.Dax + "), CALCULATE(" + counted + year + ") > 0)) + 0\n)";
        }

        /// <summary>The reading in words, said beside the figure.</summary>
        public static string Say(Read r)
        {
            var thing = r.Plural.ToLowerInvariant();
            if (r.Entity == null) return $"This counts the orders{r.YearWords}, with {r.CounterName}.";
            if (!r.Active) return $"This counts every {thing.TrimEnd('s')} in the model, once each.";
            return $"This counts the {thing} with at least one order{r.YearWords}, taking the orders from {r.CounterName}.";
        }

        private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string Forgive(string token)
        {
            if (token.Length < 5 || Vocabulary.Contains(token)) return token;
            foreach (var word in Vocabulary)
                if (Math.Abs(word.Length - token.Length) <= 1 && FrequencyReading.Distance(word, token) <= (word.Length >= 7 ? 2 : 1)) return word;
            return token;
        }
    }
}
