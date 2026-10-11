// The Plan's cleanup row: which unused columns START TICKED (THE CLEANUP ROW, round 5; Kane, 30 Sep 2026).
//
// WHAT THE ROW IS FOR. A lean model: a suggestion list of columns nothing in the model uses. It is not a Copilot check, it is
// fine on a model built from scratch, and a person reviews it before anything is removed and can untick any line or set the
// whole row aside. A column that is unused today may be wanted by a report that does not exist yet, and only the person can
// weigh that. So this file is no longer a harm gate that HIDES columns. Every column the sweep calls unused is listed, and this
// file decides two things about each one: whether it starts ticked or unticked, and the one plain reason shown on its line.
//
// HOW IT DECIDES (THE CLEANUP OFFER RULE, version 2, kept as the default pre-selection; coordinator, 29 Sep 2026; design: the
// advisor's analysis of the same day). Rounds 1 to 3 decided what a table IS and held its columns; the rule now runs the
// other way. A column starts ticked only when the shape of the suggestion holds, and every gate that lets a column through
// reads a fact TOM states: its type, the table's kind, the cardinality on both ends of each relationship, the mark, an
// aggregation mapping. A name can only leave a column UNTICKED, never tick one (the frozen belts below), except the label
// gate, which needs a name to find the table's label. A miss now costs a ticked line the person then reviews, not a delete.
//
// STARTS TICKED only when all of these hold (anything else starts unticked, with the reason):
//   S  the sweep calls it safe (the caller only asks about those).
//   C  the column is text or true/false, was made before this session, is not a key or a relationship column, is not
//      the source of a sort-by, has no data category, is in no perspective, and is not a default label or image.
//   T  its table is a plain table (no calculation group, no calculated table), takes part in at least one
//      relationship, is not marked as the date table and maps no aggregation.
//   L  the table's label is FOUND (see FindsLabel) and the column is not a possible label. The one exception is a
//      table that is only ever the many side of one-to-many relationships and holds a non-key number (a fact or a
//      bridge with amounts): its text starts ticked with no label found, but never a name column.
// and no belt holds it. Measures are not on this list: a measure's removal files on its proposer's row.
//
// Pro adds one more voice, after this one (Plan/CleanupSuggest.cs): Jev may UNTICK a column this file ticked. It can never
// tick one this file left unticked, so this file's ticked set is always the ceiling.

using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>Dispatcher thread, one per sweep. See the file comment.</summary>
    internal sealed class CleanupGuard
    {
        private readonly Session _s;
        private readonly Dictionary<Table, TableFacts> _tables = new Dictionary<Table, TableFacts>();

        internal CleanupGuard(Session s, Model m)
        {
            _s = s;
            var ends = new Dictionary<Table, List<(Column Mine, RelationshipEndCardinality MineCard, RelationshipEndCardinality OtherCard)>>();
            void End(Column c, RelationshipEndCardinality mine, RelationshipEndCardinality other)
            {
                if (c?.Table == null) return;
                if (!ends.TryGetValue(c.Table, out var list)) ends[c.Table] = list = new List<(Column, RelationshipEndCardinality, RelationshipEndCardinality)>();
                list.Add((c, mine, other));
            }
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                End(r.FromColumn, r.FromCardinality, r.ToCardinality);
                End(r.ToColumn, r.ToCardinality, r.FromCardinality);
            }
            foreach (var t in m.Tables)
                _tables[t] = new TableFacts(t, ends.TryGetValue(t, out var e) ? e : null);
        }

        /// <summary>One unused column's place on the list: whether it starts ticked, and the plain reason its line shows.
        /// <see cref="Ticked"/> is false for everything the rule would not suggest; the reason then says why (a key, a date, a
        /// name, a number, a table Semanticus cannot read). An object the ref does not resolve to is unticked too.</summary>
        internal sealed class Pick
        {
            internal bool Ticked;
            /// <summary>One plain sentence, no jargon, no em dash, ending in a full stop. Never empty.</summary>
            internal string Reason;
            /// <summary>Set when Jev unticked a column the rule ticked: how sure it was, and the engine's word for it.</summary>
            internal double? JevConfidence;
            internal string JevWord;
            internal bool JevLeftOut => JevConfidence.HasValue;
        }

        /// <summary>The reason a ticked line carries. One constant: the row's copy and the tests read it.</summary>
        internal const string TickedReason =
            "Nothing uses it, and nothing about it looks like a key, a date, a name or a number.";

        private static Pick Unticked(string why) => new Pick { Ticked = false, Reason = "Not ticked: " + why };

        /// <summary>Where <paramref name="obj"/> starts on the list. Never hides it: a column the shape or the belts would hold is
        /// still a line, unticked, with the reason.</summary>
        internal Pick Judge(ITabularNamedObject obj)
        {
            if (obj is not Column c || c.Table == null) return Unticked("Semanticus could not read it.");
            if (_s.BuiltHere(c)) return Unticked("you built it in this session.");
            if (!_tables.TryGetValue(c.Table, out var t)) return Unticked("Semanticus could not read its table.");

            // C: what kind of column.
            if (c.Type == ColumnType.RowNumber) return Unticked("it is a row number.");
            if (c.DataType == DataType.DateTime) return Unticked("it holds dates.");
            if (c.DataType != DataType.String && c.DataType != DataType.Boolean)
                return Unticked(StarterMeasureTemplates.IsNumber(c.DataType) ? "it holds numbers." : "it is not text or yes or no.");
            if (c.IsKey) return Unticked("it is a key.");
            if (t.RelationshipColumns.Contains(c)) return Unticked("a relationship uses it.");
            if (c.SortByColumn != null) return Unticked("it is set to sort by another column.");
            if (!string.IsNullOrEmpty(c.DataCategory) && !string.Equals(c.DataCategory, "Uncategorized", StringComparison.OrdinalIgnoreCase))
                return Unticked("it has a category (" + c.DataCategory + ").");
            if (c.InPerspective.Any(inPerspective => inPerspective)) return Unticked("it is in a perspective.");
            if (c.IsDefaultLabel) return Unticked("it is the default label of its table.");
            if (c.IsDefaultImage) return Unticked("it is the default image of its table.");

            // T: what kind of table.
            if (!t.Plain) return Unticked("its table is a calculation group or is built by a formula.");
            if (!t.Related) return Unticked("its table does not relate to any other table.");
            if (t.Marked) return Unticked("its table is the marked date table.");
            if (t.MapsAggregation) return Unticked("its table is part of an aggregation.");
            if (t.CalendarShaped) return Unticked("its table looks like a date table.");
            if (t.CodeShaped) return Unticked("its table looks like a code table.");

            // The belts: names that can only leave a column unticked.
            var belt = NameBelt(c, t);
            if (belt != null) return Unticked(belt);

            // L: can Semanticus tell what each row of the table is called?
            if (t.OnlyManySideWithNumber)
                return Words.HasNameWord(c) ? Unticked("it looks like a name or a description.") : new Pick { Ticked = true, Reason = TickedReason };
            if (!t.LabelFound) return Unticked("Semanticus cannot tell what each row of its table is called, so this may be the name.");
            if (t.LabelCandidates.Contains(c)) return Unticked("it may be what its table's rows are called.");
            return new Pick { Ticked = true, Reason = TickedReason };
        }

        // ---- what the Jev question needs to know about a column's table ------------------------------------------

        /// <summary>The derived facts about one column's table that the cleanup question sends and the row's reasons read. Names,
        /// types and relationship ends only, never a value.</summary>
        internal sealed class Shape
        {
            internal string Table;
            /// <summary>"lookup side" (a key of it is the one end of a one-to-many), "fact side" (only the many end), "both", "no relationships".</summary>
            internal string Role;
            internal bool HoldsNumbers;
            internal bool LabelFound;
            internal IReadOnlyList<(string Name, string Type)> Columns;
        }

        internal Shape ShapeOf(Column c)
        {
            if (c?.Table == null || !_tables.TryGetValue(c.Table, out var t)) return null;
            return new Shape
            {
                Table = c.Table.Name,
                Role = t.HasOneEnd && t.HasManyEnd ? "both ends" : t.HasOneEnd ? "lookup side (the one end)"
                     : t.HasManyEnd ? "fact side (the many end)" : t.Related ? "other relationships" : "no relationships",
                HoldsNumbers = t.HoldsNumber,
                LabelFound = t.LabelFound,
                Columns = c.Table.Columns.Where(x => x.Type != ColumnType.RowNumber)
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => (x.Name, x.DataType.ToString())).ToList(),
            };
        }

        // ---- the frozen belts ------------------------------------------------------------------------------------
        // The lists that already live in StarterMeasureTemplates (key and date words) and here (calendar and code words),
        // frozen where they are. No review round adds a word: a shape the belts miss starts ticked, and the person reviews it.

        /// <summary>The reason a name leaves a column unticked, or null when no belt holds it.</summary>
        private static string NameBelt(Column c, TableFacts t)
        {
            var words = StarterMeasureTemplates.Words(c.Name);
            if (words.Count > 0 && Words.KeyBelt.Contains(words[words.Count - 1])) return "it looks like a key.";
            if (StarterMeasureTemplates.IsDateName(c.Name)) return "it looks like a date.";
            if (words.Count > 1 && Words.DateLast.Contains(words[words.Count - 1])) return "it looks like a date.";   // HIRED_ON, CREATED_AT
            if (words.Any(Words.Calendar.Contains)) return "it looks like part of a date.";
            if (words.Any(Words.Quantity.Contains)) return "it looks like a number kept as text.";                    // a number held as text
            if (Words.NamedForTable(words, t.TableWords)) return "it is named like its table, so it may be what the rows are called.";
            return null;   // name words: held by the label gate, where the table's label is known
        }

        internal static class Words
        {
            internal static readonly HashSet<string> KeyBelt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "KEY", "ID", "SK", "PK", "FK", "GUID", "UUID", "NUMBER", "NO", "NBR", "NUM", "REF", "REFERENCE" };
            internal static readonly HashSet<string> DateLast = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ON", "AT" };
            internal static readonly HashSet<string> Calendar = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "YEAR", "YR", "FY", "FISCAL", "QTR", "QUARTER", "MONTH", "MTH", "MON", "WEEK", "WK", "WKDAY", "WEEKDAY",
                "DOW", "DAY", "HOLIDAY", "PERIOD", "SEMESTER", "CAL", "CALENDAR",
            };
            internal static readonly HashSet<string> Name = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "NAME", "NM", "TITLE", "LABEL", "DESC", "DESCR", "DESCRIPTION", "CAPTION" };
            internal static readonly HashSet<string> GenericLabel = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "NAME", "TITLE", "LABEL", "DESCRIPTION", "FULLNAME", "DISPLAYNAME" };
            internal static readonly HashSet<string> CodeTable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "REF", "REFERENCE", "CODE", "CODES", "LOOKUP", "LOOKUPS", "LKP", "LU", "XREF", "MAP", "MAPPING" };
            internal static readonly HashSet<string> CodeColumn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "CODE", "CD", "CDE" };
            internal static readonly HashSet<string> Quantity = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AMT", "AMOUNT", "QTY", "QUANTITY", "COUNT", "CNT", "TOTAL", "SUM", "PRICE", "COST", "VALUE", "VAL", "HRS",
                "HOURS", "UNITS", "RATE", "PCT", "PERCENT", "BALANCE", "REVENUE", "SALES", "MARGIN", "PROFIT", "FEE", "TAX",
                "DISCOUNT", "POINTS", "SCORE", "WEIGHT",
            };
            internal static readonly HashSet<string> TablePrefix = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "DIM", "DIMENSION", "D", "DM", "LU", "LKP", "TBL", "T" };

            internal static bool HasNameWord(Column c) =>
                StarterMeasureTemplates.Words(c.Name).Any(w => Name.Contains(w)) || EndsInName(c);

            /// <summary>The single-word names the old rule already held (USERNAME, FILENAME): kept as a belt.</summary>
            private static bool EndsInName(Column c)
            {
                var words = StarterMeasureTemplates.Words(c.Name);
                return words.Count > 0 && words[words.Count - 1].EndsWith("name", StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>Equality, a shared first four letters (Branch and Branches), or an in-order abbreviation of at
            /// least three letters with the same first letter (CLNT in CLIENT, PRJ in PROJECT). No stemming.</summary>
            internal static bool Matches(string a, string e)
            {
                if (string.Equals(a, e, StringComparison.OrdinalIgnoreCase)) return true;
                if (a.Length >= 4 && e.Length >= 4 && string.Compare(a, 0, e, 0, 4, StringComparison.OrdinalIgnoreCase) == 0) return true;
                var (s, l) = a.Length <= e.Length ? (a, e) : (e, a);
                if (s.Length < 3 || char.ToUpperInvariant(s[0]) != char.ToUpperInvariant(l[0])) return false;
                var at = 0;
                foreach (var ch in s)
                {
                    while (at < l.Length && char.ToUpperInvariant(l[at]) != char.ToUpperInvariant(ch)) at++;
                    if (at == l.Length) return false;
                    at++;
                }
                return true;
            }

            /// <summary>A column named for its own table: every word that is not a name word matches a word of the
            /// table's name, and at least one does.</summary>
            internal static bool NamedForTable(IReadOnlyList<string> words, IReadOnlyList<string> tableWords)
            {
                var rest = words.Where(w => !Name.Contains(w)).ToList();
                return rest.Count > 0 && rest.All(w => tableWords.Any(e => Matches(w, e)));
            }
        }

        // ---- what one table is made of --------------------------------------------------------------------------

        private sealed class TableFacts
        {
            internal readonly HashSet<Column> RelationshipColumns = new HashSet<Column>();
            internal readonly List<string> TableWords;
            internal bool Plain, Related, Marked, MapsAggregation, HasOneEnd, HasManyEnd, HoldsNumber;
            internal bool CalendarShaped, CodeShaped, OnlyManySideWithNumber, LabelFound;
            internal readonly HashSet<Column> LabelCandidates = new HashSet<Column>();

            internal TableFacts(Table t, List<(Column Mine, RelationshipEndCardinality MineCard, RelationshipEndCardinality OtherCard)> ends)
            {
                Plain = t is not CalculationGroupTable && t is not CalculatedTable;
                Marked = string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase);
                var columns = t.Columns.Where(c => c.Type != ColumnType.RowNumber).ToList();
                MapsAggregation = columns.Any(c => c.AlternateOf != null);
                Related = ends != null && ends.Count > 0;
                HasOneEnd = ends != null && ends.Any(e => e.MineCard == RelationshipEndCardinality.One);
                HasManyEnd = ends != null && ends.Any(e => e.MineCard == RelationshipEndCardinality.Many);
                var own = StarterMeasureTemplates.Words(t.Name);
                TableWords = own.Where(w => !Words.TablePrefix.Contains(w)).ToList();
                if (TableWords.Count == 0) TableWords = own.ToList();
                if (!Plain || !Related) return;   // nothing else is read: the table is never offered from

                // Cardinality, read on BOTH ends. A key that is the "one" end of a one-to-many looks the table up.
                var lookedUpBy = new List<Column>();      // this table's key column when it is the one side of a one-to-many
                var identity = new List<Column>();        // its relationship columns unless it is the many side of a one-to-many
                foreach (var (mine, mineCard, otherCard) in ends)
                {
                    RelationshipColumns.Add(mine);
                    var many = mineCard == RelationshipEndCardinality.Many && otherCard == RelationshipEndCardinality.One;
                    if (!many) identity.Add(mine);
                    if (mineCard == RelationshipEndCardinality.One && otherCard == RelationshipEndCardinality.Many) lookedUpBy.Add(mine);
                }

                var calendarWords = new HashSet<string>(columns
                    .Where(c => !StarterMeasureTemplates.IsDateName(c.Name))
                    .SelectMany(c => StarterMeasureTemplates.Words(c.Name)).Where(Words.Calendar.Contains), StringComparer.OrdinalIgnoreCase);
                var hasDate = columns.Any(c => c.DataType == DataType.DateTime || StarterMeasureTemplates.IsDateName(c.Name));
                var keyedByCalendar = lookedUpBy.Any(k => StarterMeasureTemplates.Words(k.Name).Any(w => Words.Calendar.Contains(w) || StarterMeasureTemplates.IsDateName(w)));
                var hierarchyCalendar = t.Hierarchies.Any(h =>
                    StarterMeasureTemplates.Words(h.Name).Any(Words.Calendar.Contains)
                    || h.Levels.Any(l => StarterMeasureTemplates.Words(l.Name).Concat(StarterMeasureTemplates.Words(l.Column?.Name)).Any(Words.Calendar.Contains)));
                CalendarShaped = keyedByCalendar || hierarchyCalendar || (hasDate && calendarWords.Count > 0) || calendarWords.Count >= 2;
                CodeShaped = StarterMeasureTemplates.Words(t.Name).Any(Words.CodeTable.Contains)
                    || lookedUpBy.Any(k => { var w = StarterMeasureTemplates.Words(k.Name); return w.Count > 0 && Words.CodeColumn.Contains(w[w.Count - 1]); });

                bool KeyShaped(Column c) => c.IsKey || RelationshipColumns.Contains(c) || StarterMeasureTemplates.IsKeyName(c.Name);
                HoldsNumber = columns.Any(c => StarterMeasureTemplates.IsNumber(c.DataType) && !KeyShaped(c));
                OnlyManySideWithNumber = identity.Count == 0 && HoldsNumber;
                if (OnlyManySideWithNumber) return;

                // The label. Entity words: the table's relationship key and IsKey columns (minus key words) and its own name.
                var stems = identity.Concat(columns.Where(c => c.IsKey))
                    .SelectMany(k => StarterMeasureTemplates.Words(k.Name))
                    .Where(w => !IsKeyWord(w)).Concat(TableWords).ToList();
                var text = columns.Where(c => c.DataType == DataType.String && !KeyShaped(c)).ToList();
                bool Strong(Column c)
                {
                    var words = StarterMeasureTemplates.Words(c.Name);
                    var rest = words.Where(w => !Words.Name.Contains(w)).ToList();
                    var namedFor = rest.Count > 0 && rest.All(w => stems.Any(e => Words.Matches(w, e)));
                    var namePlusEntity = words.Any(Words.Name.Contains) && rest.Any(w => stems.Any(e => Words.Matches(w, e)));
                    return c.IsDefaultLabel || namedFor || namePlusEntity || Words.GenericLabel.Contains(string.Concat(words));
                }
                LabelFound = text.Any(Strong);
                foreach (var c in text.Where(c => Strong(c) || Words.HasNameWord(c))) LabelCandidates.Add(c);
                if (text.Count > 0) LabelCandidates.Add(text[0]);   // the first text column that is not a key
            }

            private static bool IsKeyWord(string w) => StarterMeasureTemplates.IsKeyName(w);
        }
    }
}
