// The relationship family (T268, M4 point 2; Kane, 26 September: relationships are in this release). The plan's
// words: "which columns join, cardinality, filter direction, the active date role".
//
// WHAT IT PROPOSES. For a column that no relationship uses yet: which key column on another table it points at,
// if any, and how that relationship behaves. One Plan item per table the relationships start from ("Add 3
// relationships in Sales"), in Structure, one line per relationship. Approving the item adds every kept
// relationship in one undo step, through the same create_relationship core the op and the composer use.
//
// WHO DECIDES WHAT.
//   The ENGINE lists the options. A key is a column on another table with the same data type that has no
//   repeats: measured so on every row by the model facts when data is connected (a sample never proves it), and
//   otherwise a column the model marks as a key, names as its table's own key, or names as an identifier that is
//   not another table's own key (a "ProductKey" on Sales is a reference to Product, not a key of Sales). A key
//   the facts show repeating is never offered, whether every row was read or a sample already holds the repeat.
//   With no data read at all, a key is offered on its name and type only, and its line and check say so plainly
//   (the coordinator's decision, 26 September). A column is asked about only when its name points somewhere (an
//   identifier, a table's name, a key's name) or it is a date and the model has a date table, so the candidate
//   set is bounded and never every same-type pair.
//   JEV answers, per column: the key it joins (or none), which way a filter passes, many to one or one to one
//   (asked only when the column has no repeats either, so one to one is possible), and, when several of the
//   table's dates point at a date table, whether this one is the main date. THE PRO PATH (<see cref="ProPick"/>)
//   takes each answer only when Jev is sure of it, and the name rule's answer to that question otherwise, which
//   is exactly the path the replay scored. So every line arrives kept, and a line whose key is the rule's says so.
//   The ENGINE then decides which relationships can be ACTIVE. Two tables may be joined by at most one active
//   relationship, and no two tables may be joined by two active filter paths (Power BI refuses both). So after
//   the picks are in, relationships are added to a filter graph nearest the fact tables first, and one that
//   would make a second path arrives inactive, with a reason that says so. A pick of "both ways" that would
//   make a second path is refused. This is a check, not a choice: Jev's picks and the rule's go through the same
//   resolution. Kept picks take their places first, so a dropped pick (one a person has to keep before Approve
//   applies it) can never leave a kept relationship inactive.
//
// WHO DECIDES AT ALL. The point is relationship.join. It is live only when its replay bar is met
// (<see cref="JevDecides"/>, set from docs/redesign/jev-relationships-replay.md). Below the bar nobody asks, and
// the Plan keeps today's rule-derived relationship item ("Sort out N relationships", from the REL- readiness
// findings), exactly as before. The name-matching rule below (<see cref="RuleTarget"/>) is the baseline the
// replay scores Jev against; it proposes nothing on the Plan by itself.
//
// WHAT JEV SEES. Names, types and derived facts only (the point's five declared fields): the table's and the
// column's names, the column's data type, whether it is hidden or a key and whether it has no repeats, the
// names and types of the table's other columns, and the listed keys with their table's name, type, whether
// each has no repeats and whether its table is a date table. Never a value, a count, a minimum, a maximum or a
// sample.
//
// WHAT THIS FILE DOES NOT DO. It never asks. Prepare runs on the dispatcher and reads the model; Map and Resolve
// are pure. The asking is LocalEngine.JevFamilies.cs, through DecisionService, where every gate lives.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class RelationshipFamily
    {
        internal const string Name = "relationship";

        /// <summary>WHETHER JEV DECIDES. The contract's rule (jev-groundwork-contract.md section 2): a family's point
        /// is live only when its replay bar is met. docs/redesign/jev-relationships-replay.md sets this. False means
        /// nobody is asked and the Plan keeps today's rule-derived relationship item. 26 September: the replay failed
        /// its bar in all three runs (run 1, exact F1 0.800 against a floor of 0.85; run 2 on a fresh set, 0.722
        /// against the name rule's 0.800; run 3, the Pro path against the rule on both sets under the coordinator's
        /// rules, 67.9% against 66.4% with McNemar p 0.50 and 73.9% against 73.9% with p 1.0, and under Kane's 26
        /// September bar the win is not significant and the harm is 38 against 40 and 2 against 2), so the rule
        /// stays. Recorded at m11/m4-relationships dc33c7df; unchanged by the verifier's fixes after it.</summary>
        internal const bool JevDecides = false;

        /// <summary>The rule id a line carries when the name rule chose its key, because Jev was not sure of it.</summary>
        internal const string RuleId = "REL-NAME-RULE";

        internal const string JoinTo = "join_to";
        internal const string Direction = "filter_direction";
        internal const string Cardinality = "cardinality";
        internal const string DateRole = "date_role";

        internal const string OneWay = "one_way";
        internal const string BothWays = "both_ways";
        internal const string ManyToOne = "many_to_one";
        internal const string OneToOne = "one_to_one";
        internal const string Main = "main";
        internal const string Other = "other";

        /// <summary>The reserved "joins nothing" option. Key option ids carry <see cref="KeyPrefix"/>, so no key can
        /// ever equal it.</summary>
        internal const string None = "none";
        internal const string KeyPrefix = "key:";

        /// <summary>How many columns one run asks about, most likely first. A cap, not a sample: every ask is a call
        /// against the session's budget.</summary>
        internal const int MaxColumnsPerRun = 80;

        private const int MaxTableColumns = 40;
        private const int MaxKeysPerColumn = 16;

        // ---- what the engine lists --------------------------------------------------------------------------

        /// <summary>One column another column may join: the ONE side of a relationship.</summary>
        internal sealed class Key
        {
            public string Id;            // "key:Product/ProductKey"
            public string Ref;           // "column:Product/ProductKey"
            public string Table;
            public string Column;
            public string DataType;

            /// <summary>"yes" (measured in full, no repeats), or "not_measured". A key measured with repeats is
            /// never a Key at all.</summary>
            public string Unique;

            /// <summary>What Semanticus read of this key's values, for the check's words: <see cref="ReadEveryRow"/>,
            /// <see cref="ReadSample"/>, <see cref="ReadNoRows"/>, <see cref="ReadNotThisColumn"/> or <see cref="ReadNamesOnly"/>.
            /// Never sent.</summary>
            public string Read = ReadNamesOnly;
            public bool DateTable;
            public bool OwnKey;
            public int Order;
        }

        internal const string ReadEveryRow = "every_row";
        internal const string ReadSample = "sample";
        internal const string ReadNotThisColumn = "not_this_column";
        internal const string ReadNoRows = "no_rows";
        internal const string ReadNamesOnly = "names_only";

        internal sealed class Ask
        {
            public string Ref;
            public string ColumnName;
            public string TableRef;
            public string TableName;
            public string DataType;
            public int Order;

            /// <summary>True when the column itself has no repeats (measured in full, or a key), so one to one is
            /// possible and the cardinality question is asked.</summary>
            public bool FromUnique;
            public bool AsksDateRole;

            /// <summary>The column is a date, or its name carries a date table's name or key.</summary>
            public bool PointsAtDate;
            public List<Key> Keys = new List<Key>();
            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;

            public Key KeyById(string id) => Keys.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));
        }

        /// <summary>An active relationship already in the model, as the filter graph needs it.</summary>
        internal sealed class Edge
        {
            public string One;     // the table a filter starts from
            public string Many;    // the table it narrows
            public bool Both;
        }

        internal sealed class Prepared
        {
            public List<Ask> Asks = new List<Ask>();
            public List<Edge> Existing = new List<Edge>();

            /// <summary>Table pairs ("a\u001fb", ordinal-ignore-case sorted) that already have an active relationship.</summary>
            public HashSet<string> ActivePairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public List<ModelPlanChangeRefusal> Refused = new List<ModelPlanChangeRefusal>();
            public long Revision;
        }

        /// <summary>One chosen relationship before the engine decides whether it can be active.</summary>
        internal sealed class Pick
        {
            public Ask Ask;
            public Key Key;
            public string Direction = OneWay;
            public string Cardinality = ManyToOne;
            public bool Main;
            public double Confidence;
            public double RoleConfidence;
            public bool Active = true;
            public string InactiveWhy;

            /// <summary>The name rule chose the key, because Jev was not sure of it (the Pro path).</summary>
            public bool FromRule;

            /// <summary>At least one setting Jev was asked about (direction, cardinality, date role) is the rule's,
            /// because Jev was not sure of it; and at least one is Jev's.</summary>
            public bool SettingsFromRule;
            public bool SettingsFromJev;

            /// <summary>Whether the line arrives kept: the rule's, or Jev's at "likely" or better. Only a kept line is
            /// applied by Approve, so kept picks take their places first when the engine decides what is active.</summary>
            public bool Kept => FromRule || Confidence >= JevFamily.LikelyFloor;
        }

        // ---- names ----------------------------------------------------------------------------------------

        internal static string Norm(string s) =>
            new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        internal static string Singular(string norm)
        {
            if (norm.EndsWith("ies", StringComparison.Ordinal) && norm.Length > 4) return norm.Substring(0, norm.Length - 3) + "y";
            if (norm.EndsWith("s", StringComparison.Ordinal) && !norm.EndsWith("ss", StringComparison.Ordinal) && norm.Length > 3)
                return norm.Substring(0, norm.Length - 1);
            return norm;
        }

        private static readonly string[] IdentifierEnds = { "key", "id", "code", "number", "no", "sk", "guid" };
        private static readonly string[] OwnKeyEnds = { "key", "id", "code", "sk" };

        /// <summary>A name that says "I identify something": ends with key, id, code, number, no, sk or guid.</summary>
        internal static bool IdentifierLike(string name)
        {
            var n = Norm(name);
            return n.Length > 0 && IdentifierEnds.Any(e => n.EndsWith(e, StringComparison.Ordinal));
        }

        /// <summary>The column that names its own table's rows: "Id", "Key", the table's name, or the table's name
        /// followed by key, id, code or sk ("CustomerKey" on Customer, "Id" on Customers, "CustomerKey" on
        /// DimCustomer: a leading dim or fact is not part of what the table is called).</summary>
        internal static bool OwnKeyName(string column, string table)
        {
            var c = Norm(column);
            if (c == "id" || c == "key") return true;
            foreach (var t in TableNames(table))
            {
                var one = Singular(t);
                if (c == t || c == one) return true;
                if (OwnKeyEnds.Any(e => c == t + e || c == one + e)) return true;
            }
            return false;
        }

        /// <summary>A table's name as a column would carry it: itself, and without a leading "dim" or "fact".</summary>
        internal static IEnumerable<string> TableNames(string table)
        {
            var t = Norm(table);
            yield return t;
            foreach (var prefix in new[] { "dim", "fact" })
                if (t.StartsWith(prefix, StringComparison.Ordinal) && t.Length - prefix.Length >= 3)
                    yield return t.Substring(prefix.Length);
        }

        internal static bool DateTableLike(Table t)
        {
            if (string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)) return true;
            var n = Norm(t.Name);
            return n.Contains("date", StringComparison.Ordinal) || n.Contains("calendar", StringComparison.Ordinal);
        }

        private static bool KeyType(DataType t) => t == DataType.Int64 || t == DataType.String || t == DataType.DateTime;

        // ---- read the model once -------------------------------------------------------------------------------

        /// <summary>Read the model once, on the dispatcher: the keys, the candidate columns with the keys each may
        /// join, and the active relationships already there. The facts snapshot supplies "no repeats"; with none
        /// (no data, or not measured yet) every key reads not measured, which the request says rather than
        /// claiming.</summary>
        internal static Prepared Prepare(Model m, Facts.ModelFacts facts, int maxColumns = MaxColumnsPerRun)
        {
            var prepared = new Prepared();
            var point = DecisionRegistry.Require(DecisionRegistry.RelationshipJoin);

            var used = new HashSet<Column>();
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                if (r.FromColumn != null) used.Add(r.FromColumn);
                if (r.ToColumn != null) used.Add(r.ToColumn);
                if (!r.IsActive || r.FromTable == null || r.ToTable == null) continue;
                var (one, many) = Ends(r);
                prepared.Existing.Add(new Edge { One = one, Many = many, Both = r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections });
                prepared.ActivePairs.Add(PairKey(r.FromTable.Name, r.ToTable.Name));
            }

            var measured = MeasuredByRef(facts);
            // Whether any data was read for this model. Without it every key is offered on its name and type only, and
            // its line says so; with it, a key whose own column was not read (a DirectQuery table, one that failed)
            // says that instead, rather than claiming no data is connected.
            var dataRead = measured.Count > 0 || (facts != null && !string.Equals(facts.Data, Facts.ModelFactsData.None, StringComparison.Ordinal));
            // The names tables give their own keys ("productkey"). The same name on ANOTHER table is a reference to
            // that table, not a key of its own, so with nothing measured it is never offered as a one side.
            var ownKeyNames = new HashSet<string>(m.Tables.SelectMany(t => t.Columns.Where(c => c.Type != ColumnType.RowNumber && OwnKeyName(c.Name, t.Name))
                                                                               .Select(c => Norm(c.Name))), StringComparer.Ordinal);
            var keys = new List<(Column Col, Key Key)>();
            var order = 0;
            foreach (var t in m.Tables.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (t is CalculationGroupTable) continue;
                foreach (var c in t.Columns)
                {
                    order++;
                    if (c.Type == ColumnType.RowNumber || !KeyType(c.DataType)) continue;
                    var unique = UniqueState(c, measured);
                    if (unique == "no") continue;   // measured with repeats: never the one side
                    var own = OwnKeyName(c.Name, t.Name);
                    var isKey = unique == "yes" || c.IsKey || own
                             || (unique == "not_measured" && IdentifierLike(c.Name) && !ownKeyNames.Contains(Norm(c.Name)));
                    if (!isKey) continue;
                    keys.Add((c, new Key
                    {
                        Id = KeyPrefix + t.Name + "/" + c.Name,
                        Ref = ObjectRefs.For(c),
                        Table = t.Name,
                        Column = c.Name,
                        DataType = c.DataType.ToString(),
                        Unique = unique == "yes" ? "yes" : "not_measured",
                        Read = unique == "yes" ? ReadEveryRow
                             : measured.TryGetValue(ObjectRefs.For(c), out var mf) && mf.Sampled && mf.UniqueOnEveryRow ? ReadSample
                             : mf != null && mf.Distinct == 0 && !mf.UniqueOnEveryRow ? ReadNoRows
                             : dataRead ? ReadNotThisColumn
                             : ReadNamesOnly,
                        DateTable = DateTableLike(t),
                        OwnKey = own || c.IsKey,
                        Order = order,
                    }));
                }
            }
            var anyDateKey = keys.Any(k => k.Key.DateTable);

            var candidates = new List<(Ask Ask, int Priority)>();
            order = 0;
            foreach (var t in m.Tables.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (t is CalculationGroupTable) continue;
                foreach (var c in t.Columns)
                {
                    order++;
                    if (c.Type == ColumnType.RowNumber || !KeyType(c.DataType) || used.Contains(c)) continue;
                    if (OwnKeyName(c.Name, t.Name)) continue;   // a table's own key points at nothing
                    var options = keys
                        .Where(k => !ReferenceEquals(k.Col.Table, t) && k.Col.DataType == c.DataType && !ReferenceEquals(k.Col, c))
                        .Where(k => Points(c, k.Col, k.Key))
                        // The keys the column's name points at first, then tables' own keys, then the rest, so the cap
                        // never cuts the key the name names (AdventureWorks' Sales Territory sorts last by table).
                        .OrderByDescending(k => Affinity(c.Name, k.Key))
                        .ThenByDescending(k => k.Key.OwnKey)
                        .ThenBy(k => k.Key.Order)
                        .Take(MaxKeysPerColumn)
                        .Select(k => k.Key)
                        .ToList();
                    if (options.Count == 0) continue;
                    var priority = IdentifierLike(c.Name) ? 0 : options.Any(k => Affinity(c.Name, k)) ? 1 : 2;
                    var unique = UniqueState(c, measured);
                    candidates.Add((new Ask
                    {
                        Ref = ObjectRefs.For(c),
                        ColumnName = c.Name,
                        TableRef = ObjectRefs.For(t),
                        TableName = t.Name,
                        DataType = c.DataType.ToString(),
                        Order = order,
                        FromUnique = unique == "yes" || c.IsKey,
                        Keys = options,
                        PointsAtDate = options.Any(k => k.DateTable && (c.DataType == DataType.DateTime || Affinity(c.Name, k))),
                    }, priority));
                }
            }

            // The date role is asked when two or more of one table's candidates point at a date table: a date column,
            // or a column whose name carries the date table's name or its key's ("OrderDateKey", "ShipDate").
            foreach (var group in candidates.GroupBy(x => x.Ask.TableName, StringComparer.OrdinalIgnoreCase))
            {
                var dated = group.Where(x => x.Ask.PointsAtDate).ToList();
                if (dated.Count >= 2) foreach (var x in dated) x.Ask.AsksDateRole = true;
            }

            foreach (var (ask, _) in candidates.OrderBy(x => x.Priority).ThenBy(x => x.Ask.Order).Take(Math.Max(0, maxColumns))
                                               .OrderBy(x => x.Ask.Order))
            {
                var table = m.Tables[ask.TableName];
                var column = table.Columns[ask.ColumnName];
                ask.Questions = Questions(ask);
                ask.Snapshot = Snapshot(table, column, ask, measured, point);
                prepared.Asks.Add(ask);
            }
            return prepared;
        }

        /// <summary>Whether a column is a candidate to join a key: its name points at the key, or it is a date and
        /// the key is a date table's key. Bounded on purpose (the plan's rule: "not all same-type pairs").</summary>
        private static bool Points(Column c, Column keyColumn, Key key)
        {
            if (IdentifierLike(c.Name)) return true;
            if (Affinity(c.Name, key)) return true;
            return c.DataType == DataType.DateTime && key.DateTable;
        }

        /// <summary>The column's name carries the key's table name or the key's own name.</summary>
        private static bool Affinity(string column, Key key)
        {
            var n = Norm(column);
            var name = Norm(key.Column);
            if (TableNames(key.Table).Select(Singular).Any(t => t.Length >= 3 && n.Contains(t, StringComparison.Ordinal))) return true;
            return name.Length >= 4 && name != "id" && name != "key" && n.Contains(name, StringComparison.Ordinal);
        }

        private static (string One, string Many) Ends(SingleColumnRelationship r) =>
            r.FromCardinality == RelationshipEndCardinality.One && r.ToCardinality == RelationshipEndCardinality.Many
                ? (r.FromTable.Name, r.ToTable.Name)
                : (r.ToTable.Name, r.FromTable.Name);

        internal static string PairKey(string a, string b) =>
            string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? a + "\u001f" + b : b + "\u001f" + a;

        private static Dictionary<string, Facts.MeasuredFacts> MeasuredByRef(Facts.ModelFacts facts)
        {
            var map = new Dictionary<string, Facts.MeasuredFacts>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in facts?.Tables ?? Array.Empty<Facts.TableFacts>())
                foreach (var c in t.Columns ?? Array.Empty<Facts.ColumnFacts>())
                    if (c?.Ref != null && c.Measured != null) map[c.Ref] = c.Measured;
            return map;
        }

        /// <summary>"yes" when measured on every row with no repeats, "no" when the rows that were read already show a
        /// repeat, and "not_measured" otherwise.
        ///
        /// UniqueOnEveryRow is false when a row that was read repeats a value or is blank (LocalEngine.Facts.cs:
        /// rows read, no blanks, as many distinct values as rows). Either way the column is no clean key, and that is
        /// as true of a sample as of every row: what the unread rows hold cannot take a repeat back. So a sample that
        /// read any row and is not unique is "no", and a key it describes is never offered (the verifier's D1, 26
        /// September: a table over the sample's row limit used to keep a repeating key offerable). A sample that IS
        /// unique still proves nothing, because one unread row can repeat, so it reads "not_measured".
        ///
        /// A read that found NO rows is not a repeat either (verifier round 2, D7): an empty table is never "unique on
        /// every row" (it needs rows), yet nothing in it repeats. Distinct counts blank as a value, so no distinct
        /// value means no row was read, and the key stays offered on its name, with a check that says the table was
        /// empty.</summary>
        private static string UniqueState(Column c, Dictionary<string, Facts.MeasuredFacts> measured)
        {
            if (!measured.TryGetValue(ObjectRefs.For(c), out var f)) return "not_measured";
            if (!f.UniqueOnEveryRow) return f.Distinct > 0 ? "no" : "not_measured";
            return f.Sampled ? "not_measured" : "yes";
        }

        /// <summary>The declared snapshot for one column, through <see cref="DecisionSnapshot"/> so an undeclared
        /// field cannot be sent. Names, types, flags and "no repeats" only.</summary>
        internal static IReadOnlyDictionary<string, object> Snapshot(Table table, Column c, Ask ask,
            Dictionary<string, Facts.MeasuredFacts> measured, DecisionPoint point)
        {
            var columnFacts = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "data_type", c.DataType.ToString() },
                { "is_hidden", c.IsHidden },
                { "is_key", c.IsKey },
                { "no_repeats", UniqueState(c, measured) },
            };
            var others = table.Columns
                .Where(x => !ReferenceEquals(x, c) && x.Type != ColumnType.RowNumber)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxTableColumns)
                .Select(x => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", x.Name },
                    { "data_type", x.DataType.ToString() },
                })
                .ToList();
            var keys = ask.Keys.Select(k => (object)new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "id", k.Id },
                { "table", k.Table },
                { "column", k.Column },
                { "data_type", k.DataType },
                { "no_repeats", k.Unique },
                { "date_table", k.DateTable },
                // The facts the name rule decides on, so Jev and the rule are compared on the same facts: whether the
                // key is its table's own key, and how the column's name relates to it.
                { "own_key", k.OwnKey || OwnKeyName(k.Column, k.Table) },
                { "name_match", NameMatch(ask.ColumnName, k) },
            }).ToList();

            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("table_name", table.Name),
                new KeyValuePair<string, object>("column_name", c.Name),
                new KeyValuePair<string, object>("column_facts", columnFacts),
                new KeyValuePair<string, object>("table_columns", others),
                new KeyValuePair<string, object>("candidate_keys", keys),
            });
        }

        /// <summary>The questions for one column. join_to lists exactly this column's keys plus none; cardinality is
        /// asked only when one to one is possible; the date role only when several dates compete.</summary>
        internal static IReadOnlyList<DecisionQuestion> Questions(Ask ask)
        {
            var options = ask.Keys.Select(k => new DecisionOption(k.Id,
                    "this column holds the same values as " + k.Table + "[" + k.Column + "], and each of those values is one row of " + k.Table))
                .Concat(new[] { new DecisionOption(None, "this column is not a reference to any of the listed columns, so it joins nothing") })
                .ToList();
            var questions = new List<DecisionQuestion>
            {
                new DecisionQuestion(JoinTo, DecisionQuestionKind.Choice,
                    "Which column on another table does this column point at, so that a relationship should join them?",
                    options),
                new DecisionQuestion(Direction, DecisionQuestionKind.Choice,
                    "If it joins, which way should a filter pass?",
                    new[]
                    {
                        new DecisionOption(OneWay, "a filter on the other table narrows this table's rows and not the other way round, as a lookup table does"),
                        new DecisionOption(BothWays, "a filter on this table must also narrow the other table, as a bridge between two lookup tables needs"),
                    }),
            };
            if (ask.FromUnique)
                questions.Add(new DecisionQuestion(Cardinality, DecisionQuestionKind.Choice,
                    "If it joins, how many rows here can share one row there?",
                    new[]
                    {
                        new DecisionOption(ManyToOne, "many rows here can share one row of the other table"),
                        new DecisionOption(OneToOne, "each row here matches exactly one row of the other table, and the other way round"),
                    }));
            if (ask.AsksDateRole)
                questions.Add(new DecisionQuestion(DateRole, DecisionQuestionKind.Choice,
                    "If it joins a date table, is it the date this table's rows are normally reported by?",
                    new[]
                    {
                        new DecisionOption(Main, "it is the date reports on this table normally slice by, such as the order date"),
                        new DecisionOption(Other, "it is a secondary date, such as a ship or due date, used only in particular calculations"),
                    }));
            return questions;
        }

        // ---- Jev's answer into a pick -----------------------------------------------------------------------

        /// <summary>One column's answers into a pick, or nothing. Pure. A key that was not listed for this column,
        /// an answer to a question that was not asked, or "none" all end as no pick.</summary>
        internal static Pick Map(Ask ask, DecisionAnswers answers)
        {
            if (ask == null || answers == null || !answers.Decided) return null;
            var join = answers.For(JoinTo);
            if (join == null) return null;
            var direction = ask.Questions?.Any(q => q.Name == Direction) == true ? answers.For(Direction)?.Choice : null;
            var cardinality = ask.FromUnique ? answers.For(Cardinality)?.Choice : null;
            var role = ask.AsksDateRole ? answers.For(DateRole) : null;
            return Map(ask, join.Choice, join.Confidence, direction, cardinality, role?.Choice, role?.Confidence ?? 0);
        }

        /// <summary>THE PRO PATH: JEV WHEN SURE, ELSE THE RULE, PER QUESTION, the path the replay scored (JevRelationshipReplay,
        /// "blend") and the way the ai.schema family decides (AiSchemaFamily.ProPick). The key is Jev's when it is sure
        /// of it and the name rule's otherwise; a sure "joins nothing" is no pick, whatever the rule says. Each setting
        /// is Jev's when it is sure of it and the rule's otherwise: one way, many to one, and the main date when the
        /// rule's own key is the one joined and its name says "order". So an answer Jev is unsure of never reaches a
        /// line, and every pick arrives kept (the verifier's D4, 26 September: a sure join used to carry an unsure
        /// "both ways" into a kept line).</summary>
        internal static Pick ProPick(Ask ask, DecisionAnswers answers, double bar)
        {
            if (ask == null || answers == null || !answers.Decided) return null;
            (string Choice, double Confidence) Sure(string question)
            {
                var x = answers.For(question);
                return x != null && x.Confidence >= bar ? (x.Choice, x.Confidence) : (null, 0);
            }
            bool Asked(string question) => ask.Questions?.Any(q => q.Name == question) == true;

            var rule = RulePick(ask);
            var join = Sure(JoinTo);
            var keyId = join.Choice ?? rule?.Key.Id;
            if (keyId == null || keyId == None) return null;
            var direction = Sure(Direction);
            var cardinality = Sure(Cardinality);
            var role = Sure(DateRole);
            var ruleMain = rule != null && rule.Key.Id == keyId && rule.Main;
            var pick = Map(ask, keyId, join.Choice != null ? join.Confidence : 1.0,
                           direction.Choice ?? OneWay, cardinality.Choice ?? ManyToOne,
                           role.Choice ?? (ruleMain ? Main : Other), role.Choice != null ? role.Confidence : 1.0);
            if (pick == null) return null;
            pick.FromRule = join.Choice == null;
            var settings = new[] { (Direction, direction.Choice), (Cardinality, cardinality.Choice), (DateRole, role.Choice) }
                .Where(s => Asked(s.Item1)).ToList();
            pick.SettingsFromRule = settings.Any(s => s.Item2 == null);
            pick.SettingsFromJev = settings.Any(s => s.Item2 != null);
            return pick;
        }

        internal static Pick Map(Ask ask, string keyId, double confidence, string direction, string cardinality,
                                 string role, double roleConfidence)
        {
            if (ask == null || string.IsNullOrEmpty(keyId) || keyId == None) return null;
            var key = ask.KeyById(keyId);
            if (key == null) return null;   // never offered for this column
            return new Pick
            {
                Ask = ask,
                Key = key,
                Direction = direction == BothWays ? BothWays : OneWay,
                Cardinality = ask.FromUnique && cardinality == OneToOne ? OneToOne : ManyToOne,
                Main = ask.AsksDateRole && key.DateTable && role == Main,
                Confidence = confidence,
                RoleConfidence = roleConfidence,
            };
        }

        // ---- the rule -------------------------------------------------------------------------------------------

        /// <summary>THE RULE the replay scores Jev against, written down before the first run: join a column to the
        /// key on another table whose name it carries. First an exact name match with a table's own key
        /// ("CustomerKey" to Customer[CustomerKey], "Product" to Product[Product]), else with any listed key of
        /// the same name ("EventNo" to Events[EventNo]); then the longest own key its
        /// name ends with ("OrderDateKey" to Date[DateKey]); then the key's table name followed by the key's name
        /// ("CustomerId" to Customers[Id]). Two equally good matches mean none. One way, many to one, and among
        /// several dates pointing at one table, the one whose name says "order", else the first.</summary>
        internal static Key RuleTarget(Ask ask)
        {
            var n = Norm(ask.ColumnName);
            var own = ask.Keys.Where(k => k.OwnKey || OwnKeyName(k.Column, k.Table)).ToList();

            var exact = own.Where(k => Norm(k.Column) == n).ToList();
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1) return null;
            var sameName = ask.Keys.Where(k => Norm(k.Column) == n).ToList();
            if (sameName.Count == 1) return sameName[0];
            if (sameName.Count > 1) return null;

            var suffix = own.Where(k => Norm(k.Column).Length >= 4 && n.EndsWith(Norm(k.Column), StringComparison.Ordinal)).ToList();
            if (suffix.Count > 0)
            {
                var longest = suffix.Max(k => Norm(k.Column).Length);
                var best = suffix.Where(k => Norm(k.Column).Length == longest).ToList();
                return best.Count == 1 ? best[0] : null;
            }

            var tableAndName = own.Where(k =>
            {
                var c = Norm(k.Column);
                return TableNames(k.Table).Any(t => n == t + c || n == Singular(t) + c);
            }).ToList();
            return tableAndName.Count == 1 ? tableAndName[0] : null;
        }

        /// <summary>How a column's name relates to a key, in the rule's own terms: "same_name", "ends_with_key_name"
        /// ("OrderDateKey" and DateKey), "table_then_key_name" ("CustomerId" and Customers[Id]), "carries_table_name"
        /// ("PromoterRef" and Promoters), or "none". Worked out from names only.</summary>
        internal static string NameMatch(string column, Key k)
        {
            var n = Norm(column);
            var c = Norm(k.Column);
            if (n == c) return "same_name";
            if (c.Length >= 4 && n.EndsWith(c, StringComparison.Ordinal)) return "ends_with_key_name";
            if (TableNames(k.Table).Any(t => n == t + c || n == Singular(t) + c)) return "table_then_key_name";
            if (Affinity(column, k)) return "carries_table_name";
            return "none";
        }

        internal static Pick RulePick(Ask ask)
        {
            var key = RuleTarget(ask);
            if (key == null) return null;
            return new Pick
            {
                Ask = ask,
                Key = key,
                Main = ask.AsksDateRole && key.DateTable && Norm(ask.ColumnName).Contains("order", StringComparison.Ordinal),
                Confidence = 1.0,
            };
        }

        // ---- which relationships can be active ------------------------------------------------------------------

        /// <summary>Decide which picks can be active, and refuse what cannot be added at all. Pure.
        ///
        /// Two tables joined by an active relationship already, or by another pick that wins the pair (a kept pick
        /// first, then the main date, then the surer pick, then the earlier column), take the others inactive. Then
        /// the active picks are added to the filter graph, kept ones first and then nearest the fact tables first (a
        /// fact table is one no relationship points at),
        /// and one that would make a second filter path between two tables, or a loop, arrives inactive. A "both
        /// ways" pick that would do that is refused, because an inactive relationship's direction means nothing
        /// until a formula turns it on, and "both ways" was the point of the pick.</summary>
        internal static List<Pick> Resolve(IEnumerable<Pick> picks, IReadOnlyList<Edge> existing,
                                           ISet<string> activePairs, List<ModelPlanChangeRefusal> refused)
        {
            var all = (picks ?? Array.Empty<Pick>()).Where(p => p != null).ToList();

            // At most one relationship per column: two picks can never share a column, but guard anyway.
            all = all.GroupBy(p => p.Ask.Ref, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

            foreach (var pair in all.GroupBy(p => PairKey(p.Ask.TableName, p.Key.Table), StringComparer.OrdinalIgnoreCase))
            {
                // Kept picks first: Approve applies only kept lines, so a dropped pick that won the pair would leave the
                // kept one inactive and the pair with no active relationship at all (the verifier's D3, 26 September).
                var ordered = pair.OrderByDescending(p => p.Kept ? 1 : 0)
                                  .ThenByDescending(p => p.Main ? 1 : 0).ThenByDescending(p => p.Main ? p.RoleConfidence : 0)
                                  .ThenByDescending(p => p.Confidence).ThenBy(p => p.Ask.Order).ToList();
                var taken = activePairs != null && activePairs.Contains(pair.Key);
                foreach (var p in ordered)
                {
                    if (!taken) { taken = true; continue; }
                    p.Active = false;
                    var winner = ordered.FirstOrDefault(x => x.Active && !ReferenceEquals(x, p));
                    p.InactiveWhy = winner != null && winner.Main
                        ? winner.Ask.ColumnName + " is the main date for " + p.Ask.TableName + ", and only one relationship between two tables can be active."
                        : "Another relationship already joins " + p.Ask.TableName + " and " + p.Key.Table + ", and only one between two tables can be active.";
                }
            }

            var graph = new FilterGraph(existing);
            var depth = Depths(all, existing);
            // Kept picks take their filter paths first, for the same reason.
            foreach (var p in all.Where(p => p.Active)
                                 .OrderByDescending(p => p.Kept ? 1 : 0)
                                 .ThenBy(p => depth.TryGetValue(p.Ask.TableName, out var d) ? d : int.MaxValue)
                                 .ThenByDescending(p => p.Main ? 1 : 0)
                                 .ThenByDescending(p => p.Confidence)
                                 .ThenBy(p => p.Ask.TableName, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(p => p.Ask.Order)
                                 .ToList())
            {
                var edge = new Edge { One = p.Key.Table, Many = p.Ask.TableName, Both = p.Direction == BothWays };
                if (graph.Accepts(edge)) { graph.Add(edge); continue; }
                if (p.Direction == BothWays)
                {
                    refused?.Add(new ModelPlanChangeRefusal
                    {
                        Target = p.Ask.Ref,
                        Reason = "Joining " + p.Ask.ColumnName + " to " + p.Key.Table + "[" + p.Key.Column + "] both ways would give two "
                               + "filter paths between the same tables, so it was left out.",
                    });
                    p.Key = null;
                    continue;
                }
                p.Active = false;
                p.InactiveWhy = "Other relationships already link " + p.Key.Table + " to " + p.Ask.TableName
                              + ". A second link would make it unclear which way a filter goes.";
            }
            return all.Where(p => p.Key != null).ToList();
        }

        /// <summary>How far each table is from a fact table along the picks and the existing relationships. A fact
        /// table is one no relationship points at; it is 0, the tables it points at 1, and so on.</summary>
        private static Dictionary<string, int> Depths(IReadOnlyList<Pick> picks, IReadOnlyList<Edge> existing)
        {
            var edges = picks.Select(p => (Many: p.Ask.TableName, One: p.Key.Table))
                             .Concat((existing ?? Array.Empty<Edge>()).Select(e => (e.Many, e.One))).ToList();
            var tables = edges.SelectMany(e => new[] { e.Many, e.One }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var pointedAt = new HashSet<string>(edges.Select(e => e.One), StringComparer.OrdinalIgnoreCase);
            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            foreach (var t in tables.Where(t => !pointedAt.Contains(t))) { depth[t] = 0; queue.Enqueue(t); }
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                foreach (var e in edges.Where(e => string.Equals(e.Many, t, StringComparison.OrdinalIgnoreCase)))
                    if (!depth.ContainsKey(e.One)) { depth[e.One] = depth[t] + 1; queue.Enqueue(e.One); }
            }
            return depth;
        }

        /// <summary>The model's filter graph: an edge from the one side to the many side, and back as well when it
        /// filters both ways. An edge is accepted when it adds no second path between any two tables and no loop
        /// that were not there already.</summary>
        internal sealed class FilterGraph
        {
            private readonly Dictionary<string, List<string>> _out = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            public FilterGraph(IEnumerable<Edge> edges)
            {
                foreach (var e in edges ?? Array.Empty<Edge>()) Add(e);
            }

            public void Add(Edge e)
            {
                Link(e.One, e.Many);
                if (e.Both) Link(e.Many, e.One);
            }

            private void Link(string a, string b)
            {
                if (!_out.TryGetValue(a, out var list)) _out[a] = list = new List<string>();
                list.Add(b);
                if (!_out.ContainsKey(b)) _out[b] = new List<string>();
            }

            public bool Accepts(Edge e)
            {
                var before = Problems();
                var trial = new FilterGraph(Array.Empty<Edge>());
                foreach (var kv in _out) foreach (var b in kv.Value) trial.Link(kv.Key, b);
                trial.Add(e);
                var after = trial.Problems();
                after.ExceptWith(before);
                return after.Count == 0;
            }

            /// <summary>Every (from, to) with two or more distinct simple paths, and every table that can reach
            /// itself. Bounded: a model with a runaway graph stops counting and reports what it found.</summary>
            private HashSet<string> Problems()
            {
                var problems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var budget = 200_000;
                foreach (var start in _out.Keys.ToList())
                {
                    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var onPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
                    void Walk(string node)
                    {
                        if (--budget <= 0) return;
                        foreach (var next in _out[node])
                        {
                            if (string.Equals(next, start, StringComparison.OrdinalIgnoreCase))
                            {
                                // A loop back to the start is a problem only when it is not simply a both-ways edge
                                // walked there and back.
                                if (onPath.Count > 2) problems.Add("loop\u001f" + start);
                                continue;
                            }
                            if (onPath.Contains(next)) continue;
                            counts[next] = counts.TryGetValue(next, out var c) ? c + 1 : 1;
                            if (counts[next] >= 2) problems.Add(start + "\u001f" + next);
                            if (counts[next] > 2) continue;
                            onPath.Add(next);
                            Walk(next);
                            onPath.Remove(next);
                        }
                    }
                    Walk(start);
                }
                return problems;
            }
        }

        // ---- picks into lines -----------------------------------------------------------------------------------

        /// <summary>The Jev line for one resolved pick. Provenance on the line; reason and check built here.</summary>
        internal static ChangeItem JevLine(Pick p, double bar)
        {
            var line = Line(p);
            line.Source = JevFamily.Source;
            line.JevItem = JevFamily.ItemId(Name, p.Ask.TableRef);
            line.DecisionPoint = DecisionRegistry.RelationshipJoin;
            if (p.FromRule)
            {
                // The rule's line arrives kept and names its rule; it carries no confidence, so its provenance reads
                // "rule" and never as a Jev decision (the same shape as AiSchemaFamily's rule lines). It keeps the point,
                // so switching the point off takes it away too.
                line.Status = "approved";
                line.RuleId = RuleId;
                return line;
            }
            var word = JevFamily.ConfidenceWord(p.Confidence, bar);
            line.Status = JevFamily.StatusFor(word);
            line.DecisionConfidence = Math.Round(p.Confidence, 4);
            line.DecisionWord = word;
            return line;
        }

        internal static ChangeItem Line(Pick p)
        {
            var k = p.Key;
            var target = k.Table + "[" + k.Column + "]";
            var reason = p.Ask.ColumnName + " in " + p.Ask.TableName + " holds " + target + " values, so a relationship lets a "
                       + "filter on " + k.Table + " reach " + p.Ask.TableName + ".";
            if (p.Direction == BothWays) reason += " A filter on " + p.Ask.TableName + " also narrows " + k.Table + ".";
            if (p.Main && p.Active) reason += " It is the main date for " + p.Ask.TableName + ".";
            if (!p.Active) reason += " It arrives inactive: " + p.InactiveWhy;
            if (p.FromRule)
                reason += " Semanticus's own rule chose " + target + " from the names, because Jev was not sure which column this one points at"
                        + (p.SettingsFromJev ? "; Jev chose the settings it was sure of." : ".");
            else if (p.SettingsFromRule)
                reason += " Jev chose " + target + "; Semanticus's own rule chose the settings Jev was not sure of.";
            if (k.Read == ReadNamesOnly) reason += " This is based on names only, because no data was read for this model.";

            var why = k.OwnKey ? "it is " + k.Table + "'s own key." : "its name marks it as an identifier.";
            var check = "Both columns hold " + PlainType(p.Ask.DataType) + ". ";
            check += k.Read switch
            {
                ReadEveryRow => target + " has no repeats on any row.",
                ReadSample => "Semanticus read a sample of " + k.Table + "'s rows and found no repeats in " + target
                              + ", but a sample cannot prove every row. It is the one side because " + why,
                ReadNoRows => "Semanticus read " + k.Table + " and it has no rows yet, so it could not check " + target
                              + " for repeats. It is the one side because " + why,
                ReadNotThisColumn => "Semanticus did not read the values in " + target + ", so it could not check it for repeats. "
                                     + "It is the one side because " + why,
                _ => "This is based on names only: no data was read for this model, so Semanticus could not check " + target
                     + " for repeats. It is the one side because " + why,
            };
            if (p.Active) check += " Adding it leaves one filter path between any two tables.";

            var after = target + ", " + (p.Cardinality == OneToOne ? "one to one" : "many to one") + ", "
                      + (p.Direction == BothWays ? "filters both ways" : "filters one way") + ", " + (p.Active ? "active" : "inactive");
            return new ChangeItem
            {
                ObjectRef = p.Ask.Ref,
                ObjectName = p.Ask.ColumnName,
                Kind = "create_relationship",
                Category = "Relationships",
                Severity = "Info",
                Risk = "structural",
                Target = "relationship",
                Title = "Join to " + target + (p.Active ? "" : " (inactive)"),
                Rationale = reason,
                Before = "(none)",
                After = after,
                Reason = reason,
                CheckText = check,
                Args = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fromColumnRef"] = p.Ask.Ref,
                    ["toColumnRef"] = k.Ref,
                    ["crossFilter"] = p.Direction == BothWays ? "BothDirections" : "OneDirection",
                    ["isActive"] = p.Active ? "true" : "false",
                    ["cardinality"] = p.Cardinality == OneToOne ? "oneToOne" : "manyToOne",
                },
            };
        }

        private static string PlainType(string dataType) => dataType switch
        {
            nameof(DataType.Int64) => "whole numbers",
            nameof(DataType.String) => "text",
            nameof(DataType.DateTime) => "dates",
            _ => dataType,
        };

        /// <summary>The first "why" line of a relationship item.</summary>
        internal static string Missing(int n, string table) =>
            JevFamily.Count(n, "column", "columns") + " in " + table + (n == 1 ? " points" : " point")
            + " at another table, but no relationship joins " + (n == 1 ? "it" : "them") + " yet, so a filter on that table does not reach "
            + table + ".";
    }
}
