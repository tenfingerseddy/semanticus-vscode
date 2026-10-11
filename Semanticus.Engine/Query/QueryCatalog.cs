// What the open model offers a data question: the columns a question can split or narrow by, the measures it can
// show, and which tables filter which through ACTIVE relationships. Read once per ask from the model, on the
// dispatcher, and pure afterwards, so every check below is testable without a model.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Linguistics;

namespace Semanticus.Engine.Query
{
    internal sealed class QueryColumn
    {
        public string Table;
        public string Name;
        /// <summary>text, whole, decimal, date, truefalse or other.</summary>
        public string Kind;
        public string Description;
        public string Folder;
        /// <summary>The column's synonyms from the model's language settings. Used on this computer to shortlist
        /// splits; never sent.</summary>
        public string[] Synonyms = Array.Empty<string>();
        /// <summary>True when another column sorts by this one (MonthNumber for MonthName): a helper, not what a
        /// person reads.</summary>
        public bool SortHelper;
        /// <summary>The column the model sorts this one by (MonthNumber for MonthName), or null. A query's ORDER BY ignores it, so the
        /// template orders by it itself (Kane, 8 Oct: months in calendar order).</summary>
        public string SortByName;
        public bool Hidden;
        /// <summary>The column's own format string, kept for a date column so the grid shows the date as the model writes it.</summary>
        public string FormatString;
        /// <summary>True when a relationship uses this column. A key is never offered to match values.</summary>
        public bool Key;

        /// <summary>The DAX reference, 'Table'[Column].</summary>
        public string Dax => QueryTemplate.ColumnRef(Table, Name);
        /// <summary>The plain id both doors and the Jev options use, Table[Column].</summary>
        public string Id => Table + "[" + Name + "]";
    }

    internal sealed class QueryMeasure
    {
        public string Table;
        public string Name;
        public string Description;
        public string Folder;
        /// <summary>The measure's synonyms from the model's language settings. Used on this computer to list the
        /// measures a question most likely means first; never sent.</summary>
        public string[] Synonyms = Array.Empty<string>();
        public bool Hidden;
        /// <summary>The tables whose columns the measure reads, directly or through other measures. The home table
        /// when it reads none.</summary>
        public string[] Reads = Array.Empty<string>();
        /// <summary>The measure's DAX and its format string. Read on this computer to tell a ratio from a plain total; never sent.</summary>
        public string Expression, FormatString;

        /// <summary>True when the measure is itself a ratio: its DAX divides one aggregation of the model's data by another (both
        /// parts non-literal). A total divided by a number is not a ratio, and a percent format alone says nothing.</summary>
        public bool IsRatio => RatioParts != null;

        /// <summary>What the ratio divides, named by the measures and columns each part reads (never a value); null when the
        /// measure is not a ratio.</summary>
        public (string Numerator, string Denominator)? RatioParts
        {
            get
            {
                var split = RatioDax.Split(Expression);
                return split == null ? null : ((string, string)?)(RatioDax.Name(split.Value.Numerator), RatioDax.Name(split.Value.Denominator));
            }
        }

        public string Dax => QueryTemplate.MeasureRef(Name);
    }

    internal sealed class QueryCatalog
    {
        public List<QueryColumn> Columns = new List<QueryColumn>();
        public List<QueryMeasure> Measures = new List<QueryMeasure>();
        /// <summary>Active relationships as (many side table, one side table, filters both ways).</summary>
        public List<(string From, string To, bool Both)> Edges = new List<(string, string, bool)>();
        /// <summary>Calculation group tables, left out of the columns on purpose, so a refusal can say what they are
        /// rather than that they do not exist.</summary>
        public HashSet<string> CalculationGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Tables marked as the model's date table (data category Time): a calendar the period words are read
        /// against (QueryPeriods).</summary>
        public HashSet<string> DateTables = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Each table's synonyms from the model's language settings ("shop" for Store). Used on this computer
        /// to shortlist splits; never sent.</summary>
        public Dictionary<string, string[]> TableSynonyms = new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>Columns a person can see, in tables that are not calculation groups: the ones a question may use.</summary>
        public IEnumerable<QueryColumn> Visible => Columns.Where(c => !c.Hidden);

        public IEnumerable<QueryMeasure> VisibleMeasures => Measures.Where(m => !m.Hidden);

        public static QueryCatalog Read(Model m)
        {
            var cat = new QueryCatalog();
            // Once per culture, not once per object: the donor reader parses the whole schema again for every object
            // it is asked about, which cost this read one full parse per column, per measure and per table.
            var schemas = m.Cultures.Select(LanguageSchema.Read).ToList();
            var keys = new HashSet<(string, string)>();
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                if (r.FromColumn == null || r.ToColumn == null) continue;
                keys.Add((r.FromColumn.Table.Name, r.FromColumn.Name));
                keys.Add((r.ToColumn.Table.Name, r.ToColumn.Name));
                if (!r.IsActive) continue;
                cat.Edges.Add((r.FromColumn.Table.Name, r.ToColumn.Table.Name, r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections));
            }

            var sortHelpers = new HashSet<(string, string)>();
            foreach (var t in m.Tables)
                foreach (var c in t.Columns)
                    if (c.SortByColumn != null) sortHelpers.Add((t.Name, c.SortByColumn.Name));

            foreach (var t in m.Tables)
            {
                // A calculation group's column applies calculation items rather than naming things, so it is not a
                // column a question splits by. Left for the person's own DAX.
                if (t is CalculationGroupTable) { cat.CalculationGroups.Add(t.Name); continue; }
                if (string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)) cat.DateTables.Add(t.Name);
                var tableSynonyms = SynonymsOf(schemas, t);
                if (tableSynonyms.Length > 0) cat.TableSynonyms[t.Name] = tableSynonyms;
                foreach (var c in t.Columns)
                {
                    if (c.Type == ColumnType.RowNumber) continue;
                    cat.Columns.Add(new QueryColumn
                    {
                        Table = t.Name, Name = c.Name, Kind = KindOf(c.DataType),
                        Description = c.Description, Folder = c.DisplayFolder,
                        Hidden = c.IsHidden || t.IsHidden, Key = keys.Contains((t.Name, c.Name)), FormatString = string.IsNullOrWhiteSpace(c.FormatString) ? null : c.FormatString,
                        Synonyms = SynonymsOf(schemas, c), SortHelper = sortHelpers.Contains((t.Name, c.Name)), SortByName = c.SortByColumn?.Name,
                    });
                }
                foreach (var me in t.Measures)
                    cat.Measures.Add(new QueryMeasure
                    {
                        Table = t.Name, Name = me.Name, Description = me.Description, Folder = me.DisplayFolder,
                        Hidden = me.IsHidden, Reads = TablesRead(me), Synonyms = SynonymsOf(schemas, me),
                        Expression = me.Expression, FormatString = me.FormatString,
                    });
            }
            return cat;
        }

        /// <summary>A table's, column's or measure's terms in every culture's linguistic schema, read from schemas
        /// already parsed. Both entity shapes are taken, as the donor reader takes them: Binding at the top (what
        /// set_synonyms writes) and under Definition (what Power BI writes).</summary>
        private static string[] SynonymsOf(IReadOnlyList<LanguageSchema> schemas, TabularNamedObject c)
        {
            List<string> all = null;
            foreach (var schema in schemas)
            {
                var terms = schema?.Terms(c);
                if (string.IsNullOrWhiteSpace(terms)) continue;
                (all ?? (all = new List<string>())).AddRange(terms.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0));
            }
            return all == null ? Array.Empty<string>() : all.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>One culture's linguistic schema, parsed ONCE and then looked in. WHY: the donor reader
        /// (SynonymHelper.GetSynonyms) parses the whole schema again for every object it is asked about, so a catalog
        /// read cost one full parse per column, per measure and per table. That is objects times schema, and on a model
        /// prepared for AI both grow together, which is exactly the model a data question is asked of. Measured by the
        /// verifier on a 19-column rig with a 760 KB schema: 2754 ms for one read.
        ///
        /// The matching rules are the donor's, so the terms read are the same: Binding at the top level or under
        /// Definition; a binding with one key is a table's, and one with ConceptualEntity and ConceptualProperty is a
        /// column's or a measure's; names match exactly; the first entity in the document wins; and a term whose state
        /// is Deleted or Suggested is not a synonym. Two differences are deliberate and safer, and both only ever read
        /// more than the donor: an entity whose value is not an object, and an entity whose Definition is not an object,
        /// are each skipped here (the second read as if it had no Definition), where the donor stops at the damaged
        /// entity and every lookup after it in that culture returns nothing. One damaged entity hides only itself.</summary>
        private sealed class LanguageSchema
        {
            private readonly Dictionary<string, JObject> _tables = new Dictionary<string, JObject>(StringComparer.Ordinal);
            private readonly Dictionary<(string Table, string Member), JObject> _members = new Dictionary<(string, string), JObject>();

            /// <summary>Null when the culture holds no schema a reader can use, which reads as "no synonyms".</summary>
            public static LanguageSchema Read(Culture culture)
            {
                if (culture.ContentType != ContentType.Json || string.IsNullOrEmpty(culture.Content)) return null;
                try
                {
                    if (!(JObject.Parse(culture.Content)["Entities"] is JObject entities)) return null;
                    var s = new LanguageSchema();
                    foreach (var e in entities)
                    {
                        if (!(e.Value is JObject entity)) continue;
                        var b = entity["Binding"] as JObject ?? (entity["Definition"] as JObject)?["Binding"] as JObject;
                        if (b == null) continue;
                        if (b.Count == 1 && b["ConceptualEntity"] is JValue t)
                        {
                            if (!s._tables.ContainsKey(t.ToString())) s._tables[t.ToString()] = entity;
                        }
                        else if (b.Count == 2 && b["ConceptualEntity"] is JValue mt && b["ConceptualProperty"] is JValue mp)
                        {
                            var key = (mt.ToString(), mp.ToString());
                            if (!s._members.ContainsKey(key)) s._members[key] = entity;
                        }
                    }
                    return s;
                }
                catch { return null; }
            }

            /// <summary>The object's terms as the donor joins them, or null when it has none here.</summary>
            public string Terms(TabularNamedObject o)
            {
                JObject entity;
                switch (o)
                {
                    case Table t: _tables.TryGetValue(t.Name, out entity); break;
                    case Measure me: _members.TryGetValue((me.Table.Name, me.Name), out entity); break;
                    case Column c: _members.TryGetValue((c.Table.Name, c.Name), out entity); break;
                    default: return null;
                }
                if (entity == null) return null;
                try
                {
                    var names = new List<string>();
                    foreach (JObject term in (JArray)entity["Terms"])
                        foreach (var kv in term)
                        {
                            var p = ((JObject)kv.Value).ToObject<TermProperties>();
                            if (p.State != State.Deleted && p.State != State.Suggested) names.Add(kv.Key);
                        }
                    return string.Join(", ", names);
                }
                catch { return null; }
            }
        }

        internal static string KindOf(DataType t) => t switch
        {
            DataType.String => "text",
            DataType.Int64 => "whole",
            DataType.Double => "decimal",
            DataType.Decimal => "decimal",
            DataType.DateTime => "date",
            DataType.Boolean => "truefalse",
            _ => "other",
        };

        private static string[] TablesRead(Measure me)
        {
            var tables = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var d in me.DependsOn.Deep())
                {
                    if (d is Column c) tables.Add(c.Table.Name);
                    else if (d is Table t) tables.Add(t.Name);
                }
            }
            catch { /* an unreadable dependency tree reads as the home table below */ }
            if (tables.Count == 0) tables.Add(me.Table.Name);
            return tables.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        /// <summary>True when a filter on <paramref name="table"/> reaches rows of <paramref name="fact"/>: the same
        /// table, or one side of an active relationship walked from the many side, or either way on a relationship
        /// that filters both ways.</summary>
        public bool Filters(string table, string fact)
        {
            if (string.Equals(table, fact, StringComparison.Ordinal)) return true;
            var seen = new HashSet<string>(StringComparer.Ordinal) { fact };
            var queue = new Queue<string>();
            queue.Enqueue(fact);
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                foreach (var (from, to, both) in Edges)
                {
                    string next = null;
                    if (from == at) next = to;
                    else if (both && to == at) next = from;
                    if (next == null || !seen.Add(next)) continue;
                    if (next == table) return true;
                    queue.Enqueue(next);
                }
            }
            return false;
        }

        /// <summary>The tables a measure counts rows of, its fact tables: the tables it reads, less any that filters
        /// another table it reads, which is a dimension of that one (Product, when the measure reads Sales and uses ALL
        /// on Product). A measure that reads Sales and Budget has two. When every table filters another (both-ways
        /// relationships), all of them count.</summary>
        public string[] Roots(QueryMeasure measure) =>
            measure.Reads.Where(t => !measure.Reads.Any(o => !string.Equals(o, t, StringComparison.Ordinal) && Filters(t, o))).ToArray() is { Length: > 0 } roots
                ? roots : measure.Reads;

        /// <summary>The fact tables of the measure a column does NOT reach. Empty when the column narrows every set of
        /// rows the measure counts. A column that reaches only some of them would split one part of the number and
        /// repeat the other part's total on every row, which reads as an answer and is not one.</summary>
        public string[] Unreached(string table, QueryMeasure measure) => Roots(measure).Where(r => !Filters(table, r)).ToArray();

        /// <summary>The calculation group a reference names, or null.</summary>
        public string CalculationGroupOf(string reference) =>
            QueryTemplate.TryParseColumn(reference, out var table, out _) && CalculationGroups.Contains(table) ? table : null;

        public QueryColumn Column(string reference)
        {
            if (!QueryTemplate.TryParseColumn(reference, out var table, out var name)) return null;
            return Columns.FirstOrDefault(c => string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase)
                                            && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public QueryMeasure Measure(string reference)
        {
            var name = QueryTemplate.ParseMeasure(reference);
            return name == null ? null : Measures.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>How a column is named in a reading: its own name, or "Table Column" when another column a person can
        /// see has the same name.</summary>
        public string Shown(QueryColumn c) =>
            Visible.Count(o => string.Equals(o.Name, c.Name, StringComparison.OrdinalIgnoreCase)) > 1 ? c.Table + " " + c.Name : c.Name;
    }
}
