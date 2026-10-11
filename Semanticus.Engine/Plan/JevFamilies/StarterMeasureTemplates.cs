// The starter measure templates, and the rule that picks one (T268, jev-decision-audit.md section 5.2, X-5).
//
// ONE PLACE. Every starter measure the engine proposes is built here: the name, the DAX and the format. The
// Plan's starter family (StarterMeasuresFamily.cs) and the spec autogenerate path (LocalEngine.cs,
// AutogenerateSpecFromModel and AutogenerateSpecFromFabricAsync) both call in, so "Total Amount" means the
// same formula wherever it is proposed. Before this file the autogenerate path hardcoded SUM and the name
// "Total <column>" in two places, and nothing else could propose a starter measure at all.
//
// THE OPTIONS. none, sum, average, min, max, count (non-blank values) and distinctcount. A decision point picks
// one of these ids and nothing else; it never writes a name, a formula or a format.
//
// THE NAMES, one engine rule:
//   sum            Total <column>             (kept: it is what the spec path has always proposed)
//   average        Average <column>
//   min            Minimum <column>
//   max            Maximum <column>
//   count          Count of <column>
//   distinctcount  Distinct Count of <column>
//
// THE FORMAT. A count is a whole number, so both counts get "#,0". Every other option returns the column's own
// values, so it inherits the column's format string when the column has one, and has none otherwise.
//
// THE RULE (the baseline a decision point has to beat, and what decides when none is live). A key column, or a
// column either end of a relationship uses, gets none: nobody totals an id. Otherwise the column's own
// summarize-by setting is the author's statement of how it adds up, so it maps to the option of the same name:
// Sum to sum, Average to average, and so on, None to none. Default (never set) means sum, which is what Power BI
// does with a number column nobody configured.
// Scored against a second rule that ignores summarize-by (fact-side column: sum) on 81 labelled columns from
// three real models: this rule 61.7%, that one 25.9% (docs/redesign/jev-starter-measures-replay.md). On a new
// model the two nearly agree, because summarize-by is still Default there.
//
// WHAT COUNTS AS A CANDIDATE. A number column (whole number, decimal or fixed decimal) on a FACT table that no
// measure uses yet. A fact table is one that is on the many side of at least one relationship and on the one
// side of none, and is not the marked date table: the same Kimball reading the spec path and the Overview's
// block chart use (LocalEngine.AutogenerateSpecFromModel). "No measure uses it yet" is the model's own
// dependency list, read directly: a measure whose formula names the column. A column only reached through a
// calculated column is still a candidate, because nothing aggregates the column itself.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class StarterMeasureTemplates
    {
        internal const string None = "none";
        internal const string Sum = "sum";
        internal const string Average = "average";
        internal const string Min = "min";
        internal const string Max = "max";
        internal const string Count = "count";
        internal const string DistinctCount = "distinctcount";

        /// <summary>Every option, in the order a person reads them. The order is part of a decision request's
        /// digest, so it is fixed here and nowhere else.</summary>
        internal static readonly IReadOnlyList<string> Options = new[] { None, Sum, Average, Min, Max, Count, DistinctCount };

        /// <summary>The options that make a measure. Everything but none.</summary>
        internal static readonly IReadOnlyList<string> MeasureOptions = new[] { Sum, Average, Min, Max, Count, DistinctCount };

        /// <summary>The format every count gets: a whole number with a thousands separator.</summary>
        internal const string CountFormat = "#,0";

        internal static bool IsOption(string option) => option != null && Options.Contains(option, StringComparer.Ordinal);

        /// <summary>The measure's name for one option. Null for none.</summary>
        internal static string Name(string option, string column)
        {
            switch (option)
            {
                case Sum: return "Total " + column;
                case Average: return "Average " + column;
                case Min: return "Minimum " + column;
                case Max: return "Maximum " + column;
                case Count: return "Count of " + column;
                case DistinctCount: return "Distinct Count of " + column;
                default: return null;
            }
        }

        /// <summary>The DAX for one option. The table name is quoted and the column bracketed with their own
        /// escapes (a quote doubled, a closing bracket doubled), so a name with either in it still reads as the
        /// name. Null for none.</summary>
        internal static string Dax(string option, string table, string column)
        {
            var function = Function(option);
            return function == null ? null : function + " ( " + ColumnRef(table, column) + " )";
        }

        /// <summary>The DAX function behind one option. COUNT counts the non-blank values of the column.</summary>
        internal static string Function(string option)
        {
            switch (option)
            {
                case Sum: return "SUM";
                case Average: return "AVERAGE";
                case Min: return "MIN";
                case Max: return "MAX";
                case Count: return "COUNT";
                case DistinctCount: return "DISTINCTCOUNT";
                default: return null;
            }
        }

        internal static string ColumnRef(string table, string column) =>
            "'" + (table ?? "").Replace("'", "''") + "'[" + (column ?? "").Replace("]", "]]") + "]";

        /// <summary>The format string a new measure gets. Counts are whole numbers; the rest inherit the column's
        /// format when it has one. Null means the measure is created with no format string.</summary>
        internal static string Format(string option, string columnFormat)
        {
            if (option == Count || option == DistinctCount) return CountFormat;
            if (option == None || option == null) return null;
            return string.IsNullOrWhiteSpace(columnFormat) ? null : columnFormat;
        }

        /// <summary>The plain words for what one option does to a column, for a line's title.</summary>
        internal static string Plain(string option, string column)
        {
            switch (option)
            {
                case Sum: return "the total of " + column;
                case Average: return "the average of " + column;
                case Min: return "the lowest " + column;
                case Max: return "the highest " + column;
                case Count: return "how many rows have " + column;
                case DistinctCount: return "how many different " + column + " values there are";
                default: return "no measure";
            }
        }

        // ---- the rule ---------------------------------------------------------------------------------------

        /// <summary>The rule's choice for one column: none for a key or a relationship column, otherwise the column's
        /// summarize-by mapped to the option of the same name, with Default meaning sum, except that a column whose NAME
        /// says it is an id, a date key or a rate or price (<see cref="NeverSummed"/>), or a flag or a line number (<see cref="ColumnSummarizeFamily.FlagOrOrdinal"/>), is never totalled: it gets none
        /// where it would have got sum. An author's explicit Average or Max on it still stands.</summary>
        internal static string RuleChoice(string columnName, bool isKey, string relationshipRole, string summarizeBy)
        {
            var choice = RuleChoiceBySetting(isKey, relationshipRole, summarizeBy);
            // A flag ("IsReturn", "Weekend Flag") and a line number ("OrderLine") are never totalled either, by any path: the Pro column family already fences them
            // (ColumnSummarizeFamily.FlagOrOrdinal), and the Free start's rule proposed "Total OrderLine" and "Total IsReturn" without it (join 8, K3). No rule counts a
            // flag's true rows, so such a column gets no measure at all.
            return choice == Sum && (NeverSummed(columnName) || ColumnSummarizeFamily.FlagOrOrdinal(columnName)) ? None : choice;
        }

        private static string RuleChoiceBySetting(bool isKey, string relationshipRole, string summarizeBy)
        {
            if (isKey) return None;
            if (!string.IsNullOrEmpty(relationshipRole) && !string.Equals(relationshipRole, "none", StringComparison.OrdinalIgnoreCase))
                return None;
            switch ((summarizeBy ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "default":
                case "sum": return Sum;
                case "average": return Average;
                case "min": return Min;
                case "max": return Max;
                case "count": return Count;
                case "distinctcount": return DistinctCount;
                default: return None;   // "none", and anything this build does not know
            }
        }

        /// <summary>The rule's choice with no column name to read.</summary>
        internal static string RuleChoice(bool isKey, string relationshipRole, string summarizeBy) =>
            RuleChoice(null, isKey, relationshipRole, summarizeBy);

        internal static string RuleChoice(Column c, IReadOnlyDictionary<Column, string> roles, ISet<Column> keyParts = null)
        {
            // A declared key is every part of the key the model spec names (m11/rule-clashes), which TOM's single IsKey does not carry.
            var choice = RuleChoice(c.Name, c.IsKey || Semanticus.Analysis.SourceKeys.IsDeclaredKey(c), roles != null && roles.TryGetValue(c, out var role) ? role : "none", c.SummarizeBy.ToString());
            return choice == Sum && keyParts != null && keyParts.Contains(c) ? None : choice;
        }

        /// <summary>The columns a calculated key is built from: a calculated column that is a declared key or that a
        /// relationship uses is made of columns of its own table ("PRAC_CD" and "PRJ_SEQ" in PRAC_CD &amp; "-" &amp; PRJ_SEQ),
        /// and a total of one of those means as little as a total of the key. Read off the structure, so it holds whatever
        /// the parts are called.</summary>
        internal static HashSet<Column> CalculatedKeyParts(Model m)
        {
            var parts = new HashSet<Column>();
            void Add(Column c)
            {
                if (c is not CalculatedColumn cc) return;
                IEnumerable<Column> deps;
                try { deps = cc.DependsOn.Keys.OfType<Column>().Where(p => p.Table == c.Table).ToList(); }
                catch { return; }   // a formula that cannot be read names no parts
                foreach (var p in deps)
                    if (parts.Add(p)) Add(p);
            }
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>()) { Add(r.FromColumn); Add(r.ToColumn); }
            foreach (var t in m.Tables)
                foreach (var c in t.Columns)
                    if (c.IsKey || Semanticus.Analysis.SourceKeys.IsDeclaredKey(c)) Add(c);
            return parts;
        }

        /// <summary>True when a total of this column is refused whatever its name: a declared key, a column a
        /// relationship uses (either end), or a part of a calculated key.</summary>
        internal static bool IsKeyShaped(bool isKey, string relationshipRole, bool keyPart) =>
            isKey || keyPart || (!string.IsNullOrEmpty(relationshipRole) && !string.Equals(relationshipRole, "none", StringComparison.OrdinalIgnoreCase));

        // ---- the columns nobody totals ----------------------------------------------------------------------
        //
        // A new model's number columns are mostly not amounts. The UAT plan statement check (28 Sep) had Run the plan
        // add "Total DUE_DT_KEY" and "Total PAID_DT_KEY" (sums of yyyymmdd date keys) and the SQL draft add "Total
        // BILL_RATE" (a sum of an hourly rate): on a fresh model nothing is a relationship end or a key yet, and every
        // summarize-by is still Default, so the rule above had nothing to go on but the name, and did not read it.
        // Read by WORD, split at separators and at a lower-to-upper case change, so PAID and VALID are not ids.

        // NUMBER, NO, NBR and NUM as a LAST word are identifiers too: SalesOrderLineNumber, ORDER_NO and INV_NBR are
        // which row, not how many (verify, 28 Sep, L4). As a last word only, so "No Show Hours" still totals.
        private static readonly HashSet<string> KeyWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "KEY", "ID", "SK", "PK", "FK", "GUID", "UUID", "NUMBER", "NO", "NBR", "NUM" };
        private static readonly HashSet<string> DateWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "DT", "DATE", "YYYYMMDD", "DATEKEY" };
        private static readonly HashSet<string> RateWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "RATE", "PRICE", "PCT", "PERCENT", "PERCENTAGE", "RATIO", "%" };

        /// <summary>The words of a column name: "DUE_DT_KEY" is DUE, DT, KEY; "CustomerID" is Customer, ID.</summary>
        internal static IReadOnlyList<string> Words(string name)
        {
            var words = new List<string>();
            if (string.IsNullOrWhiteSpace(name)) return words;
            var current = new System.Text.StringBuilder();
            void Flush() { if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); } }
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (ch == '%') { Flush(); words.Add("%"); continue; }
                if (!char.IsLetterOrDigit(ch)) { Flush(); continue; }
                // "CustomerID": a capital after a small letter starts a word; "IDNumber": the last capital of a run
                // followed by a small letter does too.
                if (current.Length > 0 && char.IsUpper(ch)
                    && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                    Flush();
                current.Append(ch);
            }
            Flush();
            return words;
        }

        /// <summary>True for a column whose name says a total of it means nothing: an id or key (its last word is KEY,
        /// ID, SK...), a date held as a number (any word is DT, DATE, YYYYMMDD), or a rate or a price (any word is
        /// RATE, PRICE, PCT, PERCENT, RATIO). Starter measures never total one of these, whoever picks the option.</summary>
        internal static bool NeverSummed(string columnName)
        {
            var words = Words(columnName);
            if (words.Count == 0) return false;
            var last = LastKeyWord(words);
            // A date word ANYWHERE: OrderDateInt is a date held as a number, whatever its last word says (Sol 6.1, 1 Oct).
            return KeyWords.Contains(last) || IsDateName(columnName) || words.Any(w => RateWords.Contains(w));
        }

        /// <summary>True for a column whose name says it is an identifier: its last word is KEY, ID, NUMBER, NO...</summary>
        internal static bool IsKeyName(string columnName)
        {
            var words = Words(columnName);
            return words.Count > 0 && KeyWords.Contains(LastKeyWord(words));
        }

        // A key name that ends in a word that says which copy it is ("CustomerKeyOld", "ProductId_Prev", "OrderNo2") is still a key: a total of it means nothing.
        private static readonly HashSet<string> CopyWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "OLD", "NEW", "PREV", "PREVIOUS", "ORIG", "ORIGINAL", "COPY", "BACKUP", "BAK", "TEMP", "TMP", "ALT", "ARCHIVE", "LEGACY" };

        /// <summary>The word that decides whether the name is a key: the last one, after any trailing words that only say which copy it is (and any trailing digits).</summary>
        private static string LastKeyWord(IReadOnlyList<string> words)
        {
            var i = words.Count - 1;
            while (i > 0 && (CopyWords.Contains(words[i]) || words[i].All(char.IsDigit))) i--;
            var last = words[i].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');   // "No2" is NO with a copy number
            return last.Length > 0 ? last : words[i];
        }

        /// <summary>True for a column whose name says it holds a date, as a date or as a number: ANY of its words is
        /// DT, DATE, YYYYMMDD or DATEKEY ("CAL_DT", "DATE_KEY", "ONBRD_DT_KEY").</summary>
        internal static bool IsDateName(string columnName) => Words(columnName).Any(DateWords.Contains);

        /// <summary>True when <paramref name="m"/> is a starter measure: its name and its formula are one of the
        /// templates above over a column of its own table, whoever made it (the rule, Jev or the spec build).</summary>
        internal static bool IsStarterMeasure(Measure m)
        {
            if (m?.Table == null) return false;
            var dax = Squeeze(m.Expression);
            foreach (var c in m.Table.Columns)
                foreach (var option in MeasureOptions)
                    if (string.Equals(m.Name, Name(option, c.Name), StringComparison.OrdinalIgnoreCase)
                        && (string.Equals(dax, Squeeze(Dax(option, m.Table.Name, c.Name)), StringComparison.OrdinalIgnoreCase)
                            || string.Equals(dax, Squeeze(Function(option) + "(" + m.Table.Name + "[" + c.Name + "])"), StringComparison.OrdinalIgnoreCase)))
                        return true;
            return false;
        }

        private static string Squeeze(string dax) =>
            new string((dax ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray());

        // ---- which columns are candidates -------------------------------------------------------------------

        internal static bool IsNumber(DataType t) => t == DataType.Int64 || t == DataType.Double || t == DataType.Decimal;

        internal static bool IsNumberTypeName(string dataType) =>
            string.Equals(dataType, "Int64", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dataType, "Double", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dataType, "Decimal", StringComparison.OrdinalIgnoreCase);

        /// <summary>"one" or "many" for every column a relationship uses, with "one" winning when a column is on
        /// both ends somewhere (the same reading the model facts make, Facts/LocalEngine.Facts.cs).</summary>
        internal static Dictionary<Column, string> RelationshipRoles(Model m)
        {
            var roles = new Dictionary<Column, string>();
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                void Mark(Column c, RelationshipEndCardinality card)
                {
                    if (c == null) return;
                    var role = card == RelationshipEndCardinality.One ? "one" : "many";
                    if (!roles.TryGetValue(c, out var had) || (had == "many" && role == "one")) roles[c] = role;
                }
                Mark(r.FromColumn, r.FromCardinality);
                Mark(r.ToColumn, r.ToCardinality);
            }
            return roles;
        }

        /// <summary>The fact tables, by the one Kimball reading: on the many side of a relationship, on the one
        /// side of none, and not the marked date table.</summary>
        internal static HashSet<Table> FactTables(Model m)
        {
            var many = new HashSet<Table>();
            var one = new HashSet<Table>();
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                Table manyT, oneT;
                if (r.FromCardinality == RelationshipEndCardinality.Many) { manyT = r.FromTable; oneT = r.ToTable; }
                else if (r.ToCardinality == RelationshipEndCardinality.Many) { manyT = r.ToTable; oneT = r.FromTable; }
                else { manyT = r.FromTable; oneT = r.ToTable; }
                if (manyT != null) many.Add(manyT);
                if (oneT != null) one.Add(oneT);
            }
            var facts = new HashSet<Table>();
            foreach (var t in many)
            {
                if (one.Contains(t)) continue;
                if (string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)) continue;
                if (t is CalculationGroupTable) continue;
                facts.Add(t);
            }
            return facts;
        }

        /// <summary>Whether a measure's formula names this column directly, from the model's dependency list.</summary>
        internal static bool AnyMeasureUses(Column c)
        {
            try { return c.ReferencedBy.OfType<Measure>().Any(); }
            catch { return false; }
        }

        /// <summary>The rule this family stands for on a column: "relies on implicit aggregation" asks for a real measure over it, and the
        /// starter row is the row that adds it. A person who accepted that finding for a column (or for the whole rule) has said they read
        /// the column as it is, so the column is not a candidate: no line is offered for it, at seed or at apply.</summary>
        internal const string StandsForRuleId = "DAC-IMPLICIT-MEASURE";

        /// <summary>Every candidate column, fact table by fact table, both in name order.</summary>
        internal static IEnumerable<(Table Table, IReadOnlyList<Column> Columns)> Candidates(Model m)
        {
            var accepted = WaiverStore.Load(m);
            foreach (var t in FactTables(m).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var columns = t.Columns
                    .Where(c => c.Type != ColumnType.RowNumber && IsNumber(c.DataType) && !AnyMeasureUses(c)
                                && WaiverStore.Match(accepted, "air", StandsForRuleId, ObjectRefs.For(c)) == null)
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (columns.Count > 0) yield return (t, columns);
            }
        }

        /// <summary>Why a name cannot be used for a new measure on this table, or null when it can. A measure name
        /// is unique across the whole model, and it cannot be the name of a column on its own table either.</summary>
        internal static string NameTaken(Model m, Table table, string name)
        {
            if (m.AllMeasures.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                return "A measure named " + name + " already exists in this model, so no second one was proposed.";
            if (table.Columns.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                return table.Name + " already has a column named " + name + ", so a measure cannot take that name.";
            return null;
        }

        internal static string Percent(double share) =>
            Math.Round(share * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}
