// The date table family (T268, M4 point 3; Kane, 26 September: the date table is in this release, with its own
// replay). docs/redesign/jev-plan.md, M4: "the date table: needed, which table, marked".
//
// WHAT IT DECIDES. Once per model that has no marked date table and whose formulas use time intelligence: which table
// is the date table (one of the tables the engine lists) or a new generated one. The replay asked three decisions
// (needed, which, mark); only "which" cleared Kane's bar, so needed stays the rule's (DATE-MARK) and the engine always
// marks. One Plan item, in
// Structure: "Mark Date as the date table" or "Add a date table". It never builds relationships: joining a new
// date table to the facts is the relationships family's point (m11/m4-relationships), not this one.
//
// WHICH TABLES THE ENGINE LISTS. A table is an option only when all of this holds, checked here and never guessed:
//   - it is not Power BI's own auto date table and not a calculation group;
//   - it is not on the many side of any relationship (a fact table's date is never the date table);
//   - it has one date column the engine can name as its key: the one measured with no repeats and no missing day,
//     else the one already flagged as the key, else the one the model's own formulas read, else its only date
//     column. Two date columns and nothing to tell them apart is not an option; it is refused with the reason;
//   - that column's dates are not MEASURED to repeat or to miss a day. Measured means the model facts read every
//     row (Facts/LocalEngine.Facts.cs); a sample proves neither, and a model opened from files has no data, so
//     there the facts say "not measured" and the line's check says so. A calculated table built by CALENDAR or
//     CALENDARAUTO has one row per day by construction, which the engine reads off its formula.
// "generate" (a new table, the same one generate_date_table makes) and "none" are always offered.
//
// WHAT JEV SEES. Names, types and derived facts only (the point's two declared fields): each table's name, its
// side of its relationships and its column names and types; for each date column whether it is the key, whether
// its dates repeat, whether a day is missing, and whether a calendar formula builds it; and whether the model's
// formulas use time intelligence. Never a date, a first or last day, a row count or any value.
//
// WHAT THE ENGINE DOES WITH THE ANSWER. A listed table becomes a mark_date_table line on its date column (the same
// mark the direct op makes, MarkDateTableCore), "generate" becomes a generate_date_table line whose range the engine
// writes from the model's own fact date columns, and "none", or a table Jev chose not to mark, is no line at all.
// A pick that was never offered is no line.
//
// WHO DECIDES. The point is date.table; <see cref="JevDecides"/> records the replay verdict, and today it is OFF: the
// rule decides for everyone, nobody is asked, and get_decision_points and What Pro decides say the point is not on
// yet. When it is on, Pro with permission has Jev pick the table, and only a SURE pick becomes a line; otherwise, and
// for free, the rule decides exactly as before: DATE-MARK and PlanDateMark make the "Mark the date table" row
// (LocalEngine.Plan.cs, DateTableItem), which stands aside only while a sure Jev line is waiting. A free session never
// asks.
//
// WHAT THIS FILE DOES NOT DO. It never asks. Prepare runs on the dispatcher and reads the model; Map is pure.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Analysis;
using Semanticus.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class DateTableFamily
    {
        internal const string Name = "date-table";

        internal const string TableQuestion = "date_table";

        internal const string None = "none";
        internal const string Generate = "generate";
        internal const string TableOptionPrefix = "table:";

        /// <summary>WHETHER JEV DECIDES WHICH TABLE. The contract's rule (jev-groundwork-contract.md section 2): a decision
        /// is Jev's only when its replay clears the bar. Kane's switch-on bar (26 September): the Jev-when-sure path beats
        /// the rule, McNemar p below 0.05, and no more harmful mistakes than the rule. OFF (joint verifier and coordinator,
        /// 27 September; docs/redesign/jev-datetable-replay.md): on the 11 models the product actually asks about (time
        /// intelligence used, no table marked, at least one table to offer) Jev and the rule as it ships are both right 11
        /// of 11, McNemar p 1.0, so the bar is not met. Round 3's "33 of 33 against 9" counted 22 models the product never
        /// asks about and 2 misses this lane fixed in the rule. Whether one is needed stays the rule's (DATE-MARK) and
        /// marking stays the engine's. True would let Jev pick the table for Pro again.</summary>
        internal const bool JevDecides = false;

        /// <summary>The generated table's name, or the first free "Date N".</summary>
        internal const string GeneratedName = "Date";

        private const int MaxTables = 60;
        private const int MaxColumnsPerTable = 30;

        private static readonly string[] CalendarFunctions = { "CALENDAR", "CALENDARAUTO" };

        /// <summary>What the engine knows about one date column. Null means not measured, which is never a pass and
        /// never a fail.</summary>
        internal sealed class DateColumn
        {
            public string Ref;
            public string Name;
            public bool IsKey;
            public bool? Repeats;
            public bool? MissingDays;
            public bool BuiltByCalendar;
        }

        internal sealed class Candidate
        {
            public string TableRef;
            public string TableName;
            public DateColumn Key;
        }

        internal sealed class Ask
        {
            public string ModelName;
            public bool UsesTimeIntelligence;
            public List<Candidate> Candidates = new List<Candidate>();

            /// <summary>The name "generate" would use, free in this model.</summary>
            public string GeneratedTable;

            /// <summary>The DAX start and end of the generated table's range, written by the engine from the fact
            /// tables' date columns, or null for generate_date_table's own default range.</summary>
            public string StartExpr;
            public string EndExpr;

            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;
        }

        internal sealed class Prepared
        {
            public Ask Ask;
            public List<ModelPlanChangeRefusal> Refused = new List<ModelPlanChangeRefusal>();
            public long Revision;
        }

        // ---- read the model once --------------------------------------------------------------------------

        /// <summary>The one ask for this model, or none. Asked only where the rule says a date table is needed (DATE-MARK: the
        /// formulas use time intelligence and no table is marked) AND the engine can offer at least one table: with none
        /// to offer, a new one is the only answer and nobody's choice, so the rule's row keeps it.</summary>
        internal static Prepared Prepare(Model m, Facts.ModelFacts facts)
        {
            var prepared = new Prepared();
            if (m == null) return prepared;
            if (m.Tables.Any(t => string.Equals(t.DataCategory, "Time", StringComparison.Ordinal))) return prepared;

            var tables = m.Tables.Where(t => !ReadinessRuleSet.IsAutoDateTable(t) && !(t is CalculationGroupTable))
                                 .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var usesTi = ReadinessRuleSet.UsesTimeIntelligence(m);
            if (!usesTi) return prepared;   // needed is the rule's decision

            var measured = MeasuredByRef(facts);
            var readByDax = DateColumnsReadByDax(m);
            var manySide = new HashSet<Table>(m.Relationships.OfType<SingleColumnRelationship>().Select(r => r.FromTable).Where(t => t != null));
            var oneSide = new HashSet<Table>(m.Relationships.OfType<SingleColumnRelationship>().Select(r => r.ToTable).Where(t => t != null));

            var ask = new Ask { ModelName = m.Name, UsesTimeIntelligence = usesTi };
            foreach (var t in tables)
            {
                if (manySide.Contains(t)) continue;
                var dates = DateColumnsOf(t).Select(c => Describe(t, c, measured)).ToList();
                if (dates.Count == 0) continue;
                var key = KeyOf(dates, readByDax, out var ambiguous);
                if (key == null)
                {
                    if (ambiguous)
                        prepared.Refused.Add(new ModelPlanChangeRefusal
                        {
                            Target = ObjectRefs.For(t),
                            Reason = t.Name + " has " + dates.Count.ToString(CultureInfo.InvariantCulture)
                                   + " date columns and nothing says which one is its key, so it was not offered as the date table.",
                        });
                    continue;
                }
                if (key.Repeats == true || key.MissingDays == true)
                {
                    prepared.Refused.Add(new ModelPlanChangeRefusal
                    {
                        Target = ObjectRefs.For(t),
                        Reason = t.Name + "[" + key.Name + "] "
                               + (key.Repeats == true ? "has the same date on more than one row" : "is missing some days between its first and last date")
                               + ", so it cannot be the date table and was not offered.",
                    });
                    continue;
                }
                ask.Candidates.Add(new Candidate { TableRef = ObjectRefs.For(t), TableName = t.Name, Key = key });
            }

            if (ask.Candidates.Count == 0) return prepared;
            ask.GeneratedTable = FreeName(m);
            (ask.StartExpr, ask.EndExpr) = Range(m, manySide);
            ask.Questions = Questions(ask.Candidates);
            ask.Snapshot = Snapshot(m, tables, ask, manySide, oneSide, measured, readByDax);
            prepared.Ask = ask;
            return prepared;
        }

        private static IEnumerable<Column> DateColumnsOf(Table t) =>
            t.Columns.Where(c => c.DataType == DataType.DateTime && c.Type != ColumnType.RowNumber)
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, Facts.MeasuredFacts> MeasuredByRef(Facts.ModelFacts facts)
        {
            var byRef = new Dictionary<string, Facts.MeasuredFacts>(StringComparer.OrdinalIgnoreCase);
            if (facts == null || facts.Data == Facts.ModelFactsData.None) return byRef;
            foreach (var c in facts.Tables.SelectMany(t => t.Columns))
                if (c?.Ref != null && c.Measured != null && !c.Measured.Sampled) byRef[c.Ref] = c.Measured;
            return byRef;
        }

        private static DateColumn Describe(Table t, Column c, Dictionary<string, Facts.MeasuredFacts> measured)
        {
            var d = new DateColumn { Ref = ObjectRefs.For(c), Name = c.Name, IsKey = c.IsKey };
            if (measured.TryGetValue(d.Ref, out var f))
            {
                d.Repeats = !f.UniqueOnEveryRow;
                d.MissingDays = f.ContiguousDays.HasValue ? !f.ContiguousDays.Value : (bool?)null;
            }
            // CALENDAR and CALENDARAUTO make one row per day, every day, in a column named Date. Read off the formula,
            // so it holds with no data at all; a measured fact above still wins where there is one.
            if (t is CalculatedTable ct && string.Equals(c.Name, "Date", StringComparison.OrdinalIgnoreCase) && BuiltByCalendar(ct.Expression))
            {
                d.BuiltByCalendar = true;
                d.Repeats ??= false;
                d.MissingDays ??= false;
            }
            return d;
        }

        internal static bool BuiltByCalendar(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return false;
            var toks = DaxScan.Tokenize(expression);
            return CalendarFunctions.Any(fn => DaxScan.CallsFunction(toks, fn));
        }

        /// <summary>The table's key date column, in the order this file's header gives. Null with
        /// <paramref name="ambiguous"/> set when two or more are left and nothing tells them apart.</summary>
        internal static DateColumn KeyOf(IReadOnlyList<DateColumn> dates, ISet<string> readByDax, out bool ambiguous)
        {
            ambiguous = false;
            if (dates.Count == 0) return null;
            var proven = dates.Where(d => d.Repeats == false && d.MissingDays == false).ToList();
            if (proven.Count == 1) return proven[0];
            var keys = dates.Where(d => d.IsKey).ToList();
            if (keys.Count == 1) return keys[0];
            var read = dates.Where(d => readByDax.Contains(d.Ref)).ToList();
            if (read.Count == 1) return read[0];
            if (dates.Count == 1) return dates[0];
            ambiguous = true;
            return null;
        }

        /// <summary>Date columns some measure, calculated column or calculated table reads, by ref.</summary>
        private static HashSet<string> DateColumnsReadByDax(Model m)
        {
            var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var objects = m.AllMeasures.Cast<IDaxDependantObject>()
                .Concat(m.AllColumns.OfType<CalculatedColumn>())
                .Concat(m.Tables.OfType<CalculatedTable>())
                .Concat(m.CalculationGroups.SelectMany(g => g.CalculationItems));
            foreach (var o in objects)
            {
                IEnumerable<IDaxObject> keys;
                try { keys = o.DependsOn.Keys.ToArray(); }
                catch { continue; }
                foreach (var c in keys.OfType<Column>())
                    if (c.DataType == DataType.DateTime) read.Add(ObjectRefs.For(c));
            }
            return read;
        }

        private static string FreeName(Model m)
        {
            if (!m.Tables.Contains(GeneratedName)) return GeneratedName;
            for (var i = 2; ; i++)
            {
                var name = GeneratedName + " " + i.ToString(CultureInfo.InvariantCulture);
                if (!m.Tables.Contains(name)) return name;
            }
        }

        /// <summary>The generated table's range, as DAX over the fact tables' own date columns: from the first of January
        /// of the earliest year to the thirty-first of December of the latest. Only DAX leaves this method; no date is
        /// read. No fact date column means null, which is generate_date_table's own default range.</summary>
        internal static (string Start, string End) Range(Model m, ISet<Table> manySide)
        {
            var cols = manySide.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                               .SelectMany(t => DateColumnsOf(t)).Take(4).ToList();
            if (cols.Count == 0) return (null, null);
            string Fold(string fn) =>
                cols.Select(c => fn + "(" + Facts.FactsMeasurer.QuoteColumn(c.Table.Name, c.Name) + ")")
                    .Aggregate((a, b) => fn + "(" + a + ", " + b + ")");
            return ("DATE(YEAR(" + Fold("MIN") + "), 1, 1)", "DATE(YEAR(" + Fold("MAX") + "), 12, 31)");
        }

        // ---- what Jev is sent ------------------------------------------------------------------------------

        private static string Fact(bool? v) => v == null ? "not measured" : v.Value ? "yes" : "no";

        internal static IReadOnlyDictionary<string, object> Snapshot(Model m, IReadOnlyList<Table> tables, Ask ask,
            ISet<Table> manySide, ISet<Table> oneSide, Dictionary<string, Facts.MeasuredFacts> measured, ISet<string> readByDax)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.DateTable);
            var list = tables.Take(MaxTables).Select(t =>
            {
                var side = manySide.Contains(t) && oneSide.Contains(t) ? "both sides"
                         : manySide.Contains(t) ? "many side"
                         : oneSide.Contains(t) ? "one side"
                         : "no relationships";
                var dates = DateColumnsOf(t).Select(c => Describe(t, c, measured)).Select(d => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", d.Name },
                    { "is_key", d.IsKey },
                    { "dates_repeat", Fact(d.Repeats) },
                    { "days_missing", Fact(d.MissingDays) },
                    { "built_by_calendar_formula", d.BuiltByCalendar },
                    { "read_by_formulas", readByDax.Contains(d.Ref) },
                }).ToList();
                return (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", t.Name },
                    { "relationships", side },
                    { "related_from", m.Relationships.OfType<SingleColumnRelationship>()
                                        .Where(r => ReferenceEquals(r.ToTable, t) && r.FromTable != null)
                                        .Select(r => r.FromTable.Name).Distinct(StringComparer.Ordinal)
                                        .OrderBy(n => n, StringComparer.Ordinal).ToList() },
                    { "is_calculated", t is CalculatedTable },
                    { "columns", t.Columns.Where(c => c.Type != ColumnType.RowNumber)
                                  .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                                  .Take(MaxColumnsPerTable)
                                  .Select(c => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                                  {
                                      { "name", c.Name },
                                      { "data_type", c.DataType.ToString() },
                                  }).ToList() },
                    { "date_columns", dates },
                };
            }).ToList();

            var modelFacts = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "uses_time_intelligence", ask.UsesTimeIntelligence },
                { "tables", tables.Count },
                { "relationships", m.Relationships.Count },
            };
            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("model_tables", list),
                new KeyValuePair<string, object>("model_facts", modelFacts),
            });
        }

        /// <summary>The one question Jev is asked: which table, or a new one. Whether a date table is needed is the rule's
        /// (DATE-MARK), and marking is always on: in the replay's round 3 Jev did not clear the bar on either
        /// (docs/redesign/jev-datetable-replay.md), so neither is asked.</summary>
        internal static IReadOnlyList<DecisionQuestion> Questions(IReadOnlyList<Candidate> candidates)
        {
            var options = new List<DecisionOption>
            {
                new DecisionOption(Generate, "none of the model's tables is a date table, so a new one is added"),
            };
            foreach (var c in candidates)
                options.Add(new DecisionOption(TableOptionPrefix + c.TableName,
                    c.TableName + " is the model's date table: one row per day, used to report by day, month and year"));
            return new[]
            {
                new DecisionQuestion(TableQuestion, DecisionQuestionKind.Choice,
                    "This model needs a date table. Which table should it be?", options),
            };
        }

        // ---- Jev's answer into a line -------------------------------------------------------------------------

        internal static void Map(Ask ask, DecisionAnswers answers, double bar, List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused)
        {
            if (ask == null || answers == null || !answers.Decided) return;
            var table = answers.For(TableQuestion);
            if (table == null) return;
            // JEV WHEN SURE, ELSE THE RULE (Kane's switch-on bar): below the point's bar Jev proposes nothing, and the
            // rule's "Mark the date table" row stays the date table row. Marking is always on: it is the engine's.
            if (table.Confidence < bar) return;
            Map(ask, table.Choice, table.Confidence, true, 1, bar, lines, refused);
        }

        /// <summary>One pick into at most one line. Pure: no model, no ask. The line's confidence is the lower of the two
        /// answers, because the line is only right when both are.</summary>
        internal static void Map(Ask ask, string choice, double confidence, bool? markIt, double markConfidence, double bar,
                                 List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused)
        {
            if (ask == null || string.IsNullOrEmpty(choice) || string.Equals(choice, None, StringComparison.Ordinal)) return;

            if (string.Equals(choice, Generate, StringComparison.Ordinal))
            {
                var marked = markIt != false;
                var sure = markIt == null ? confidence : Math.Min(confidence, markConfidence);
                var word = JevFamily.ConfidenceWord(sure, bar);
                var range = ask.StartExpr == null
                    ? "the five years to the end of this year"
                    : "the years the model's own dates cover";
                var reason = (ask.Candidates.Count == 0 ? "No table in this model can be its date table" : "None of this model's tables looks like its date table")
                           + (ask.UsesTimeIntelligence ? ", and its formulas use time intelligence, which needs one." : ", so a new one with one row per day is added.");
                lines.Add(new ChangeItem
                {
                    ObjectRef = "table:" + ask.GeneratedTable,
                    ObjectName = ask.GeneratedTable,
                    Kind = "generate_date_table",
                    Source = JevFamily.Source,
                    Category = "Modeling",
                    Severity = "High",
                    Risk = "safe",
                    Target = "generate_date_table",
                    Title = "Add " + ask.GeneratedTable + (marked ? " and mark it as the date table" : " as a date table"),
                    Rationale = reason,
                    Before = "(none)",
                    After = ask.GeneratedTable,
                    Args = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["tableName"] = ask.GeneratedTable,
                        ["startExpr"] = ask.StartExpr,
                        ["endExpr"] = ask.EndExpr,
                        ["markAsDate"] = marked ? "true" : "false",
                    },
                    Status = JevFamily.StatusFor(word),
                    JevItem = JevFamily.ItemId(Name, "table:" + ask.GeneratedTable),
                    DecisionPoint = DecisionRegistry.DateTable,
                    DecisionConfidence = Math.Round(sure, 4),
                    DecisionWord = word,
                    Reason = reason,
                    CheckText = "Semanticus makes it with a formula (CALENDAR) over " + range + ". So it has one row per day and no day is "
                              + "missing. It is not joined to any table yet.",
                });
                return;
            }

            if (!choice.StartsWith(TableOptionPrefix, StringComparison.Ordinal)) return;
            var name = choice.Substring(TableOptionPrefix.Length);
            var candidate = ask.Candidates.FirstOrDefault(c => string.Equals(c.TableName, name, StringComparison.Ordinal));
            if (candidate == null) return;   // never offered
            if (markIt == false) return;     // the table is already what it is; leaving it unmarked writes nothing

            var sureMark = markIt == null ? confidence : Math.Min(confidence, markConfidence);
            var w = JevFamily.ConfidenceWord(sureMark, bar);
            var key = candidate.Key;
            // SAY ONLY WHAT WAS CHECKED (joint verifier DT3). "One row per day" is a claim about the dates, so the reason
            // makes it only when the check below proves it: a calendar formula, or every row measured. Otherwise the
            // reason says what the engine did see, which is why the table could be offered at all.
            var oneRowPerDay = key.Repeats == false && key.MissingDays == false;
            var why = (oneRowPerDay
                        ? candidate.TableName + " has one row per day in " + key.Name
                        : candidate.TableName + " is not on the many side of any relationship, and " + key.Name + " is its date column")
                    + (ask.UsesTimeIntelligence ? ". Date measures such as year to date in this model need a marked date table." : ". Marking it gives the model one date table for reports to use.");
            string check;
            if (key.BuiltByCalendar && oneRowPerDay)
                check = candidate.TableName + " is built by a calendar formula, so " + key.Name + " has every day once.";
            else if (oneRowPerDay)
                check = key.Name + " was measured on every row: no date repeats and no day is missing.";
            else if (key.Repeats == false)
                check = key.Name + " was measured on every row and no date repeats, but Semanticus could not measure whether a day "
                      + "is missing. Power BI checks it when the model is next refreshed.";
            else
                check = "Semanticus could not measure " + key.Name + ", so it does not know yet whether a date repeats or a day is "
                      + "missing. Power BI checks it when the model is next refreshed.";
            lines.Add(new ChangeItem
            {
                ObjectRef = candidate.TableRef,
                ObjectName = candidate.TableName,
                Kind = "mark_date_table",
                Source = JevFamily.Source,
                Category = "Modeling",
                Severity = "High",
                Risk = "safe",
                Target = "dateTable",
                Title = "Mark " + candidate.TableName + " as the date table, on " + key.Name,
                Rationale = why,
                Before = "(not marked)",
                After = key.Name,
                Status = JevFamily.StatusFor(w),
                JevItem = JevFamily.ItemId(Name, candidate.TableRef),
                DecisionPoint = DecisionRegistry.DateTable,
                DecisionConfidence = Math.Round(sureMark, 4),
                DecisionWord = w,
                Reason = why,
                CheckText = check,
            });
        }

        /// <summary>The item's title, from its one line: a mark names the table, a new table says so.</summary>
        internal static string Title(IReadOnlyList<ChangeItem> lines, string table)
        {
            var line = lines?.FirstOrDefault();
            return line != null && string.Equals(line.Kind, "generate_date_table", StringComparison.Ordinal)
                ? "Add a date table"
                : "Mark " + table + " as the date table";
        }

        /// <summary>The first "why" line of a date table item.</summary>
        internal static string Missing(int n, string table) =>
            "No table in this model is marked as the date table. Power BI groups dates with its own hidden tables, "
            + "and date measures such as year to date have no date table to use.";
    }
}
