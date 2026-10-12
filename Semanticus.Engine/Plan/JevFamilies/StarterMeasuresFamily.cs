// The starter measures family (T268, jev-decision-audit.md section 5.2 and X-5). Kane, 24 September: "with
// measures, Jev should be able to decide for a new model, what aggregation to use and whether the column needs
// a base aggregation measure or not."
//
// WHAT IT PROPOSES. For every number column on a fact table that no measure uses yet: whether it gets a starter
// measure, and which one (StarterMeasureTemplates.cs has the seven options, the names, the DAX and the format).
// One Plan item per fact table ("Add 4 starter measures in Sales"), in Meaning beside the measures family, with
// up to two lines per column: create the measure, and give it its format when it has one. Both lines of one
// column arrive kept or dropped together.
//
// SUPERSEDED (Kane's redesign, 26 September). One decision now covers a number column's base measure: column.summarize
// (ColumnSummarizeFamily.cs), which sets the column not to add up and picks its explicit measure. measure.starter lost
// its replay and stays off (JevDecides false); while column.summarize is live the rule items below stand aside too
// (LocalEngine.Plan.cs, StarterByJev), so a column never gets two proposals. Free sessions, and Pro ones where
// column.summarize is off, still see the rule items. Its templates, names and DAX check are the ones the new family uses.
//
// WHO DECIDES. The point is measure.starter. It is live only when its replay bar is met
// (<see cref="JevDecides"/>, set from docs/redesign/jev-starter-measures-replay.md). When it is live and the
// session is Pro with permission, Jev picks one option per column through the family runner
// (LocalEngine.JevFamilies.cs) and the item is Jev's. Otherwise the RULE decides (StarterMeasureTemplates.
// RuleChoice), the item is an ordinary rule item ("starter/table:Sales", owned by auto) and every line says so:
// provenance kind "rule", rule STARTER-MEASURE. A free session sees the rule item exactly as it sees every
// other rule item on the Plan today, and never asks Jev, not even to count.
//
// WHAT JEV SEES. Names, types, settings and derived facts only (the point's four declared fields): the table
// and column names, the table's other columns with their types, and the column's data type, key flag,
// relationship end, summarize-by, format, hidden flag, whether it is calculated, and three shares worked out on
// this computer from the model facts (how many values are different, how many are blank, whether all are whole
// numbers). Never a value, a minimum, a maximum or a sample: MeasuredFacts carries none, and this file copies
// named fields only.
//
// WHAT THIS FILE DOES NOT DO. It never asks. Prepare and RuleLines run on the dispatcher and read the model; Map
// is pure. Every line's DAX goes through the model's own DAX check before it is offered (Validate), and again
// at apply time through CreateMeasureCore.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Analysis;
using Semanticus.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class StarterMeasuresFamily
    {
        internal const string Name = "starter";

        /// <summary>The rule id every rule-decided line carries, so its provenance names a rule.</summary>
        internal const string RuleId = "STARTER-MEASURE";

        /// <summary>The id prefix of the rule item: "starter/table:Sales".</summary>
        internal const string RuleItemPrefix = "starter/";

        internal const string Question = "aggregation";

        /// <summary>WHETHER JEV DECIDES. The contract's rule (jev-groundwork-contract.md section 2): a family's
        /// point is live only when its replay bar is met. The replay (docs/redesign/jev-starter-measures-replay.md)
        /// sets this. False means the rule decides for everyone and Jev is never asked.</summary>
        internal const bool JevDecides = false;

        /// <summary>How many columns one run asks about. A cap, not a sample, for the same reason as the measures
        /// family's: every ask is a call against the session's budget.</summary>
        internal const int MaxColumnsPerRun = 60;

        private const int MaxTableColumns = 40;

        internal static bool IsRuleItem(string id) => id != null && id.StartsWith(RuleItemPrefix, StringComparison.Ordinal);

        internal static string RuleItemId(string tableName) => RuleItemPrefix + "table:" + tableName;

        /// <summary>Whether a change plan line is one of this family's, either way it was decided.</summary>
        internal static bool IsStarterLine(ChangeItem line) =>
            line != null
            && (string.Equals(line.RuleId, RuleId, StringComparison.Ordinal)
                || (line.JevItem ?? "").StartsWith(JevFamily.ItemPrefix + Name + ".", StringComparison.Ordinal));

        // ---- one column, everything Map needs -------------------------------------------------------------

        internal sealed class Ask
        {
            public string Ref;               // the column
            public string ColumnName;
            public string TableRef;
            public string TableName;
            public string ColumnFormat;
            public string SummarizeBy;
            public bool IsKey;
            public string Role;
            /// <summary>A part of a calculated key: not in the snapshot (it is the model's structure, not a setting Jev reads).</summary>
            public bool KeyPart;
            public string RuleOption;
            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;

            /// <summary>Why each option's name cannot be used, for the options whose name is already taken.</summary>
            public Dictionary<string, string> Taken = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>The ref of the measure each option would create. Every one of them, so a column Jev did not
            /// answer this time keeps whichever line was already waiting for it.</summary>
            public IEnumerable<string> MeasureRefs =>
                StarterMeasureTemplates.MeasureOptions.Select(o => MeasureRef(TableName, StarterMeasureTemplates.Name(o, ColumnName)));
        }

        internal sealed class Prepared
        {
            public List<Ask> Asks = new List<Ask>();
            public List<ModelPlanChangeRefusal> Refused = new List<ModelPlanChangeRefusal>();
            public long Revision;
        }

        internal static string MeasureRef(string table, string name) => "measure:" + table + "/" + name;

        /// <summary>Why a starter measure is not offered again: the model's History shows it was removed and the removal
        /// stands (LocalEngine.RemovedMeasureRefs). Without this, a person who deleted "Total DUE_DT_KEY" was offered it
        /// again on the very next read, and a person following the Plan never reached an end (UAT, 28 Sep). Undoing the
        /// removal puts the measure back, so there is nothing to offer then either. Null when it may be offered.</summary>
        internal static string RemovedBefore(ISet<string> removed, string table, string name) =>
            removed != null && removed.Contains(MeasureRef(table, name))
                ? name + " was removed from this model before, so it is not offered again."
                : null;

        /// <summary>How a column the person gave no measure in the start plan is written in the same set as the removed measures (UAT-J7-S2-009).</summary>
        internal static string DeclinedKey(string columnRef) => "declined:" + columnRef;

        /// <summary>True when the person refused a measure for this column when they built the model (<see cref="DeclinedKey"/>). No family offers one for it again.</summary>
        internal static bool IsRefused(ISet<string> removed, Column c) => removed != null && removed.Contains(DeclinedKey(ObjectRefs.For(c)));

        /// <summary>Read the model once, on the dispatcher, and build one ask per candidate column. The facts
        /// snapshot supplies the derived shares; with none (no data, or not measured yet) the shares are absent,
        /// which the request says rather than sending zeros.</summary>
        internal static Prepared Prepare(Model m, Facts.ModelFacts facts, ISet<string> removed = null)
        {
            var prepared = new Prepared();
            var roles = StarterMeasureTemplates.RelationshipRoles(m);
            var keyParts = StarterMeasureTemplates.CalculatedKeyParts(m);
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureStarter);
            var questions = Questions();
            var asked = 0;

            foreach (var (table, columns) in StarterMeasureTemplates.Candidates(m))
            {
                foreach (var c in columns)
                {
                    if (IsRefused(removed, c)) continue;
                    if (asked >= MaxColumnsPerRun) return prepared;
                    asked++;
                    var ask = AskFor(m, table, c, roles, keyParts, facts, point, removed);
                    ask.Questions = questions;
                    prepared.Asks.Add(ask);
                }
            }
            return prepared;
        }

        private static Ask AskFor(Model m, Table table, Column c, IReadOnlyDictionary<Column, string> roles, ISet<Column> keyParts,
                                  Facts.ModelFacts facts, DecisionPoint point, ISet<string> removed = null)
        {
            var role = roles.TryGetValue(c, out var r) ? r : "none";
            var ask = new Ask
            {
                Ref = ObjectRefs.For(c),
                ColumnName = c.Name,
                TableRef = ObjectRefs.For(table),
                TableName = table.Name,
                ColumnFormat = string.IsNullOrWhiteSpace(c.FormatString) ? null : c.FormatString,
                SummarizeBy = c.SummarizeBy.ToString(),
                IsKey = SourceKeys.IsDeclaredKey(c),   // every part of a declared key, not only the one TOM holds
                Role = role,
                KeyPart = keyParts.Contains(c),
                RuleOption = StarterMeasureTemplates.RuleChoice(c, roles, keyParts),
            };
            foreach (var option in StarterMeasureTemplates.MeasureOptions)
            {
                var name = StarterMeasureTemplates.Name(option, c.Name);
                var taken = StarterMeasureTemplates.NameTaken(m, table, name) ?? RemovedBefore(removed, table.Name, name);
                if (taken != null) ask.Taken[option] = taken;
            }
            if (point != null) ask.Snapshot = Snapshot(table, c, role, facts, point);
            return ask;
        }

        /// <summary>The declared snapshot for one column, built through <see cref="DecisionSnapshot"/> so an
        /// undeclared field cannot be sent. Every entry is a name, a type, a setting or a derived share.</summary>
        internal static IReadOnlyDictionary<string, object> Snapshot(Table table, Column c, string role, Facts.ModelFacts facts, DecisionPoint point)
        {
            var columnRef = ObjectRefs.For(c);
            var tableFacts = facts?.Tables?.FirstOrDefault(t => string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase));
            var measured = tableFacts?.Columns?.FirstOrDefault(x => string.Equals(x.Ref, columnRef, StringComparison.OrdinalIgnoreCase))?.Measured;

            object measuredFacts = null;
            if (measured != null)
            {
                var rows = tableFacts.Rows;
                measuredFacts = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "distinct_share", rows.HasValue && rows.Value > 0 ? Math.Round(Math.Min(1.0, measured.Distinct / (double)rows.Value), 3) : (object)null },
                    { "blank_share", Math.Round(measured.BlankShare, 3) },
                    { "whole_numbers", measured.WholeNumbers },
                    { "unique_on_every_row", measured.UniqueOnEveryRow },
                    { "sampled", measured.Sampled },
                };
            }

            var columnFacts = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "data_type", c.DataType.ToString() },
                { "is_key", c.IsKey },
                { "relationship_role", role },
                { "summarize_by", c.SummarizeBy.ToString() },
                { "format_string", string.IsNullOrWhiteSpace(c.FormatString) ? null : c.FormatString },
                { "is_hidden", c.IsHidden },
                { "is_calculated", c is CalculatedColumn },
                { "measured", measuredFacts },
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

            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("table_name", table.Name),
                new KeyValuePair<string, object>("column_name", c.Name),
                new KeyValuePair<string, object>("column_facts", columnFacts),
                new KeyValuePair<string, object>("table_columns", others),
            });
        }

        /// <summary>The criteria describe the column and what people ask of it, never the product's state.</summary>
        private static readonly Dictionary<string, string> Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { StarterMeasureTemplates.None, "the column is an identifier, a code, a key, a date part or a setting, so nobody adds it up, averages it or counts it on its own" },
            { StarterMeasureTemplates.Sum, "the values add up to a meaningful total across rows, such as an amount sold, a quantity or a cost" },
            { StarterMeasureTemplates.Average, "the values are rates, unit prices, scores or shares, where a total means nothing but a typical value does" },
            { StarterMeasureTemplates.Min, "the smallest value is what people ask about, such as the lowest price or the earliest reading" },
            { StarterMeasureTemplates.Max, "the largest value is what people ask about, such as a peak or the highest reading" },
            { StarterMeasureTemplates.Count, "people ask how many rows have a value in this column" },
            { StarterMeasureTemplates.DistinctCount, "people ask how many different values appear, such as how many customers or products" },
        };

        internal static IReadOnlyList<DecisionQuestion> Questions() => new[]
        {
            new DecisionQuestion(Question, DecisionQuestionKind.Choice,
                "Does this number column need a starter measure, and which one?",
                DecisionRegistry.StarterAggregations.Select(o => new DecisionOption(o, Criteria[o])).ToList()),
        };

        // ---- Jev's answer into lines ------------------------------------------------------------------------

        /// <summary>Turn one column's answer into lines, or a refusal. Pure: no model, no ask.</summary>
        internal static void Map(Ask ask, DecisionAnswers answers, double bar, List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused)
        {
            if (ask == null || answers == null || !answers.Decided) return;
            var answer = answers.For(Question);
            var option = answer?.Choice;
            if (!StarterMeasureTemplates.IsOption(option) || option == StarterMeasureTemplates.None) return;
            // Jev may pick a total for a column whose name says it is an id, a date key or a rate: the same fence the
            // rule has, so no path adds "Total DUE_DT_KEY" (StarterMeasureTemplates.NeverSummed).
            if (option == StarterMeasureTemplates.Sum && StarterMeasureTemplates.NeverSummed(ask.ColumnName))
            {
                refused.Add(new ModelPlanChangeRefusal { Target = ask.Ref, Reason = ask.ColumnName + " is an id, a date key or a rate, so a total of it would mean nothing. No measure was added." });
                return;
            }
            // And the same fence by STRUCTURE, whatever the column is called: a key, a column a relationship uses (either
            // end) or a part of a calculated key is never totalled (the rule gives it no measure, RuleChoice).
            if (option == StarterMeasureTemplates.Sum && StarterMeasureTemplates.IsKeyShaped(ask.IsKey, ask.Role, ask.KeyPart))
            {
                refused.Add(new ModelPlanChangeRefusal { Target = ask.Ref, Reason = ask.ColumnName + " is a key or a column a relationship uses, so a total of it would mean nothing. No measure was added." });
                return;
            }
            if (ask.Taken.TryGetValue(option, out var taken))
            {
                refused.Add(new ModelPlanChangeRefusal { Target = ask.Ref, Reason = taken });
                return;
            }

            var word = JevFamily.ConfidenceWord(answer.Confidence, bar);
            var itemId = JevFamily.ItemId(Name, ask.TableRef);
            foreach (var line in Lines(ask, option, JevReason(ask, option)))
            {
                line.Source = JevFamily.Source;
                line.Status = JevFamily.StatusFor(word);
                line.JevItem = itemId;
                line.DecisionPoint = DecisionRegistry.MeasureStarter;
                line.DecisionConfidence = Math.Round(answer.Confidence, 4);
                line.DecisionWord = word;
                lines.Add(line);
            }
        }

        // ---- the rule ---------------------------------------------------------------------------------------

        /// <summary>The rule's lines for one fact table, read off the model. The rule item's propose calls this
        /// (LocalEngine.PlanVerbs.cs, CollectLinesForItem). Every line is kept: a rule has no confidence to be
        /// unsure with, and undo is the safety net.</summary>
        internal static void RuleLines(Model m, string tableName, List<ChangeItem> seeds, List<ModelPlanChangeRefusal> refused, ISet<string> removed = null)
        {
            var roles = StarterMeasureTemplates.RelationshipRoles(m);
            var keyParts = StarterMeasureTemplates.CalculatedKeyParts(m);
            foreach (var (table, columns) in StarterMeasureTemplates.Candidates(m))
            {
                if (!string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var c in columns)
                {
                    var ask = AskFor(m, table, c, roles, keyParts, null, null, removed);
                    if (ask.RuleOption == StarterMeasureTemplates.None) continue;
                    if (ask.Taken.TryGetValue(ask.RuleOption, out var taken))
                    {
                        refused.Add(new ModelPlanChangeRefusal { Target = ask.Ref, Reason = taken });
                        continue;
                    }
                    foreach (var line in Lines(ask, ask.RuleOption, RuleReason(ask)))
                    {
                        line.Source = "deterministic";
                        line.RuleId = RuleId;
                        line.Status = "approved";
                        line.Reason = null;   // a rule line's why is its rationale; Reason is the Jev line's field
                        seeds.Add(line);
                    }
                }
            }
            Validate(m, seeds, refused);
        }

        /// <summary>What the rule would propose, table by table, for the Plan's rule items. Only the tables with
        /// at least one line; the measure names are the ones the lines would create.</summary>
        internal static List<(string Table, List<(string Column, string Measure)> Measures)> RuleSummary(Model m, ISet<string> removed = null)
        {
            var summary = new List<(string, List<(string, string)>)>();
            var roles = StarterMeasureTemplates.RelationshipRoles(m);
            var keyParts = StarterMeasureTemplates.CalculatedKeyParts(m);
            foreach (var (table, columns) in StarterMeasureTemplates.Candidates(m))
            {
                var measures = new List<(string, string)>();
                foreach (var c in columns)
                {
                    if (IsRefused(removed, c)) continue;
                    var option = StarterMeasureTemplates.RuleChoice(c, roles, keyParts);
                    if (option == StarterMeasureTemplates.None) continue;
                    var name = StarterMeasureTemplates.Name(option, c.Name);
                    if (StarterMeasureTemplates.NameTaken(m, table, name) != null) continue;
                    if (RemovedBefore(removed, table.Name, name) != null) continue;
                    measures.Add((c.Name, name));
                }
                if (measures.Count > 0) summary.Add((table.Name, measures));
            }
            return summary;
        }

        // ---- lines, reasons, the check ------------------------------------------------------------------------

        /// <summary>The one or two lines for one column and one option: create the measure, then give it its
        /// format when it has one. Provenance is stamped by the caller.</summary>
        private static IEnumerable<ChangeItem> Lines(Ask ask, string option, string reason)
        {
            var name = StarterMeasureTemplates.Name(option, ask.ColumnName);
            var measureRef = MeasureRef(ask.TableName, name);
            var dax = StarterMeasureTemplates.Dax(option, ask.TableName, ask.ColumnName);
            yield return new ChangeItem
            {
                ObjectRef = measureRef,
                ObjectName = name,
                Kind = "create_measure",
                Category = "Measures",
                Severity = "Info",
                Risk = "safe",
                Target = "expression",
                Title = "Add as " + StarterMeasureTemplates.Plain(option, ask.ColumnName),
                Rationale = reason,
                Before = "(none)",
                After = dax,
                Reason = reason,
                CheckText = name + " is a new name in this model, and its formula passed the model's DAX check.",
                // Never offered back as unused: it was added on purpose, and nothing uses a new measure yet.
                Args = new Dictionary<string, string>(StringComparer.Ordinal) { ["addedOnPurpose"] = "the starter measure for " + ask.ColumnName + " in " + ask.TableName },
            };

            var format = StarterMeasureTemplates.Format(option, ask.ColumnFormat);
            if (format == null) yield break;
            yield return new ChangeItem
            {
                ObjectRef = measureRef,
                ObjectName = name,
                Kind = "set_measure_format",
                Category = "Measures",
                Severity = "Info",
                Risk = "safe",
                Target = "formatString",
                Title = "Format as " + format,
                Rationale = reason,
                Before = "(none)",
                After = format,
                Reason = reason,
                CheckText = option == StarterMeasureTemplates.Count || option == StarterMeasureTemplates.DistinctCount
                    ? "A count is a whole number, so it shows no decimal places."
                    : ask.ColumnName + " already shows as " + format + ", and the new measure takes the same format.",
            };
        }

        /// <summary>The DAX check, on the dispatcher, before a line is offered. A line whose formula the model
        /// refuses is never offered: its lines go and the column goes on the refused list with the reason.</summary>
        internal static void Validate(Model m, List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused)
        {
            var bad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Where(l => string.Equals(l.Kind, "create_measure", StringComparison.Ordinal)))
            {
                string problem = null;
                try
                {
                    // The same bar CreateMeasureCore holds a write to (RefuseDaxForWrite): any diagnostic at all.
                    var result = DaxValidator.Validate(m, line.After);
                    if (result?.Diagnostics?.Length > 0) problem = result.Diagnostics[0].Message;
                }
                catch (Exception ex) { problem = ex.Message; }
                if (problem == null) continue;
                bad.Add(line.ObjectRef);
                refused.Add(new ModelPlanChangeRefusal
                {
                    Target = line.ObjectRef,
                    Reason = "The formula for " + line.ObjectName + " did not pass the model's DAX check, so it was left out.",
                });
            }
            if (bad.Count > 0) lines.RemoveAll(l => bad.Contains(l.ObjectRef ?? ""));
        }

        /// <summary>The first "why" line of a starter item, Jev's or the rule's: the same gap, said once.</summary>
        internal static string Missing(int n, string table) =>
            table + " has number columns that no measure uses yet. A report adds each one up in whatever way "
            + "the column is set to. " + JevFamily.Count(n, "simple measure", "simple measures")
            + " would give each one a formula you can see.";

        private static string Base(Ask ask) =>
            ask.ColumnName + " is a number column in " + ask.TableName + " that no measure uses yet.";

        private static string JevReason(Ask ask, string option)
        {
            string clause;
            switch (option)
            {
                case StarterMeasureTemplates.Sum: clause = "Its values add up across rows, so its total is the first thing people ask of it."; break;
                case StarterMeasureTemplates.Average: clause = "A total of it means little, so its average is what people ask for."; break;
                case StarterMeasureTemplates.Min: clause = "Its lowest value is the one people ask about."; break;
                case StarterMeasureTemplates.Max: clause = "Its highest value is the one people ask about."; break;
                case StarterMeasureTemplates.Count: clause = "People ask how many rows have it, so this counts them."; break;
                case StarterMeasureTemplates.DistinctCount: clause = "People ask how many different ones there are, so this counts each value once."; break;
                default: clause = ""; break;
            }
            var agrees = option == ask.RuleOption && ask.RuleOption != StarterMeasureTemplates.None
                ? " Its own default summary says the same." : "";
            return Base(ask) + " " + clause + agrees;
        }

        private static string RuleReason(Ask ask)
        {
            var plain = StarterMeasureTemplates.Plain(ask.RuleOption, ask.ColumnName);
            if (string.Equals(ask.SummarizeBy, "Default", StringComparison.OrdinalIgnoreCase))
                return Base(ask) + " It has no default summary set, and a number column adds up unless told otherwise, so the rule adds " + plain + ".";
            return Base(ask) + " Its default summary is " + Lower(Spaced(ask.SummarizeBy)) + ", so the rule adds " + plain + ".";
        }

        private static string Lower(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        private static string Spaced(string s) =>
            string.Equals(s, "DistinctCount", StringComparison.OrdinalIgnoreCase) ? "distinct count" : s;
    }
}
