// The column format family (T268, M4 columns first; Kane, 25 September: "stick to the plan").
//
// WHY THIS SETTING, AND ONLY THIS ONE. The column replay (docs/redesign/jev-columns-replay.md) asked Jev all five
// column settings on 668 columns. Format was the one it shipped: Jev with names only was right 98% of the time,
// the stronger rule 81%. Hidden stays on its rule (the rule won). Summarize by, data category and sort by stay on
// theirs too: the second run (26 September, 54 public models) gave each enough labels, and each lost to or tied a
// free rule reading the same facts. So this family decides format and nothing else about a column.
//
// WHAT JEV SEES. Names and types only, because that arm won: the table's name, the column's name, its data type
// and whether it is calculated, and the names and types of the table's other columns. No derived facts (the
// replay's facts arm did slightly worse), and never a value.
//
// WHICH COLUMNS. Exactly FMT-COLUMN's population (ReadinessRuleSet.ColumnFormatApplies) that has no format yet: a
// visible number or date column that is not a key, not a relationship end and not named like an identifier. The
// rule and the family therefore name the same columns, and a line applied here clears the rule's finding.
//
// WHAT JEV CHOOSES AND WHAT THE ENGINE DOES WITH IT. One of the six format families, the same question and the
// same criteria the replay asked. The engine then checks the pick against the options it lists for this
// column's type (a number never gets a date format, a date never gets a number format) and turns the family into
// a real format string: the first static format of the matching category in the format library
// (format-templates.json, the same library the picker reads), or, for money, the money format this model ALREADY
// uses. It never guesses a currency. A pick that fails any of this is never a line; it goes on the refused list
// with a plain reason. "text_or_none" proposes nothing.
//
// WHAT THIS FILE DOES NOT DO. It never asks. Prepare runs on the dispatcher and reads the model; Map is pure. The
// asking is LocalEngine.JevFamilies.cs, through DecisionService, where every gate lives (Pro, consent, budget,
// the fence).

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class ColumnFormatFamily
    {
        internal const string Name = "column-format";

        internal const string Question = "format_family";

        /// <summary>WHETHER THE ASKS GO ONE PER TABLE (m11/jev-batch, JevTableBatch.cs): a table's columns in one request, its
        /// column names once, one question per column pointing at its entry in `columns`. True, as shipped: ON (Kane, 1 Oct: "the batching is the best mitigation not caps"). The comparison run of 30 September missed its fixed bar (docs/redesign/jev-batch-comparison-result.md: the unsure middle moves, sure answers stay), and Kane accepted that an unsure answer can move and arrive ticked or unticked in the reviewed Plan. JevPerObjectModeTests still proves both modes give the same plan lines. False: one request per object (the test seam JevBatchedForTest sets it).</summary>
        internal const bool Batched = true;

        /// <summary>How many columns one batched request asks about. Names and types only, so a chunk is small.</summary>
        internal const int ChunkSize = 30;

        /// <summary>How many columns one run ASKS about. A cap on the asks, never on what is seen (m11/sf-progress): Prepare lists
        /// every eligible column and LocalEngine.JevFamilies.cs asks this many per run, not-yet-asked first, then starts the next
        /// run for the rest. Every ask is a call against the session's budget.</summary>
        internal const int MaxColumnsPerRun = 60;

        private const int MaxTableColumns = 40;

        private static readonly char[] MoneySymbols = { '$', '€', '£', '¥', '₹' };

        internal sealed class Ask
        {
            public string Ref;
            public string ColumnName;
            public string TableRef;
            public string TableName;
            public string DataType;

            /// <summary>The column's format as it is now: blank, or only blank spaces a person typed. The line's Before shows it as it is.</summary>
            public string CurrentFormat;

            /// <summary>The money format this model already uses most (measures and columns), or null.</summary>
            public string MoneyFormat;
            public int MoneyFormatUses;

            public IReadOnlyDictionary<string, object> Snapshot;
            public IReadOnlyList<DecisionQuestion> Questions;

            /// <summary>This column's entry in a batched request's `columns`: its data type and whether it is calculated.</summary>
            public Dictionary<string, object> Entry;

            /// <summary>Whether the format fill rule proves this column is a whole-number quantity that may be filled with a
            /// whole-number format (FormatFill.MayFill, asked once in Prepare). A pick the engine cannot prove is a
            /// suggestion, never an approved fill.</summary>
            public bool MayFillWholeNumber;

            /// <summary>What the column computed when this ask was prepared (FormatFill.ComputeDigest): the stamp its format line carries
            /// (addendum 4 item 23).</summary>
            public string Digest;
        }

        internal sealed class Prepared
        {
            public List<Ask> Asks = new List<Ask>();
            public List<ModelPlanChangeRefusal> Refused = new List<ModelPlanChangeRefusal>();
            public long Revision;

            /// <summary>Each table's column names and types, by table ref: the batched request's one copy.</summary>
            public Dictionary<string, List<object>> TableColumns = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        }

        // ---- the options the engine lists ---------------------------------------------------------------

        private static readonly string[] NumberOptions = { "currency", "percent", "whole_number", "decimal", "text_or_none" };
        private static readonly string[] DateOptions = { "date_or_time", "text_or_none" };

        /// <summary>The format families that fit a column of this data type, in the registry's order. Everything Jev
        /// picks is checked against this list before it can become a line.</summary>
        internal static IReadOnlyList<string> OptionsFor(string dataType)
        {
            switch (dataType)
            {
                case nameof(DataType.Int64):
                case nameof(DataType.Double):
                case nameof(DataType.Decimal):
                    return DecisionRegistry.FormatFamilies.Where(NumberOptions.Contains).ToArray();
                case nameof(DataType.DateTime):
                    return DecisionRegistry.FormatFamilies.Where(DateOptions.Contains).ToArray();
                default:
                    return Array.Empty<string>();
            }
        }

        /// <summary>The catalog category behind each family the engine can fill in from the library. Money is absent
        /// on purpose: it comes from the model's own money format, never a catalog currency.</summary>
        private static readonly Dictionary<string, string> FamilyCategory = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["whole_number"] = "Whole numbers",
            ["decimal"] = "Decimals",
            ["percent"] = "Percentages",
            ["date_or_time"] = "Dates",
        };

        // ---- read the model once --------------------------------------------------------------------------

        internal static Prepared Prepare(Model m)
        {
            var prepared = new Prepared();
            var point = DecisionRegistry.Require(DecisionRegistry.ColumnFormat);
            var questions = Questions();

            var money = m.Tables.SelectMany(t => t.Measures.Select(x => x.FormatString).Concat(t.Columns.Select(x => x.FormatString)))
                .Where(fmt => !string.IsNullOrWhiteSpace(fmt) && fmt.IndexOfAny(MoneySymbols) >= 0)
                .GroupBy(fmt => fmt, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .FirstOrDefault();

            // A change the person accepted is left alone, like every other path that writes or queues one (m11/rule-clashes: a waiver
            // declines a CHANGE, and FMT-COLUMN or SEM_COLUMN_NEEDS_FORMAT_STRING accepted on a column declines its format string).
            // Not asked at all, so a declined column costs no call and never reaches Jev's row as a suggestion to tick.
            var declined = DeclinedChanges.Read(m);
            foreach (var table in m.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var c in table.Columns.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!Wants(c)) continue;
                    if (declined.Declines(ObjectRefs.For(c), "formatString")) continue;
                    var tableRef = ObjectRefs.For(table);
                    if (!prepared.TableColumns.ContainsKey(tableRef))
                        prepared.TableColumns[tableRef] = table.Columns.Where(x => x.Type != ColumnType.RowNumber)
                            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(MaxTableColumns)
                            .Select(x => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                { "name", x.Name },
                                { "data_type", x.DataType.ToString() },
                            }).ToList();
                    prepared.Asks.Add(new Ask
                    {
                        Ref = ObjectRefs.For(c),
                        ColumnName = c.Name,
                        TableRef = ObjectRefs.For(table),
                        TableName = table.Name,
                        DataType = c.DataType.ToString(),
                        CurrentFormat = c.FormatString ?? "",
                        MoneyFormat = money?.Key,
                        MoneyFormatUses = money?.Count() ?? 0,
                        Snapshot = Snapshot(table, c, point),
                        Entry = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "name", c.Name },
                            { "data_type", c.DataType.ToString() },
                            { "is_calculated", c is CalculatedColumn },
                        },
                        Questions = questions,
                        MayFillWholeNumber = FormatFill.MayFill(c, "#,0"),
                        Digest = FormatFill.ComputeDigest(c),
                    });
                }
            }
            return prepared;
        }

        /// <summary>A column this family asks about: FMT-COLUMN's population, with no format yet.</summary>
        internal static bool Wants(Column c) =>
            ReadinessRuleSet.ColumnFormatApplies(c) && string.IsNullOrWhiteSpace(c.FormatString) && FormatFill.HoldsNoFormatText(c);

        /// <summary>The declared snapshot for one column, through <see cref="DecisionSnapshot"/> so an undeclared
        /// field cannot be sent. Names and types only.</summary>
        internal static IReadOnlyDictionary<string, object> Snapshot(Table table, Column c, DecisionPoint point)
        {
            var columnFacts = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "data_type", c.DataType.ToString() },
                { "is_calculated", c is CalculatedColumn },
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

        /// <summary>The replay's own criteria (tools/jev-replay/columns/rules.mjs, FORMAT_CRITERIA), word for word,
        /// so the question asked here is the one the replay scored.</summary>
        private static readonly Dictionary<string, string> Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "currency", "the values are displayed with a currency symbol" },
            { "percent", "the values are displayed as a percentage with a percent sign" },
            { "whole_number", "the values are displayed as numbers with no decimal places" },
            { "decimal", "the values are displayed as numbers with one or more decimal places" },
            { "date_or_time", "the values are displayed as a date or a time" },
            { "text_or_none", "the values are text rather than numbers" },
        };

        internal const string Instructions = "How are this column's values displayed to a report reader?";

        internal static IReadOnlyList<DecisionQuestion> Questions() => new[]
        {
            new DecisionQuestion(Question, DecisionQuestionKind.Choice, Instructions,
                DecisionRegistry.FormatFamilies.Select(o => new DecisionOption(o, Criteria[o])).ToList()),
        };

        /// <summary>The batched requests for the asks: each table's columns in chunks, the table's column names and types once, a
        /// question for each column that starts "For the column `columns[i]`:" and then says what the per-column question says.</summary>
        internal static List<TableBatch<Ask>> Batches(Prepared prepared, int size = ChunkSize)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.ColumnFormat);
            var options = DecisionRegistry.FormatFamilies.Select(o => new DecisionOption(o, Criteria[o])).ToList();
            TableBatch<Ask> Build(IReadOnlyList<Ask> chunk)
            {
                var batch = new TableBatch<Ask> { TableRef = chunk[0].TableRef, Asks = chunk.ToList() };
                var questions = new List<DecisionQuestion>();
                for (var i = 0; i < chunk.Count; i++)
                {
                    var name = JevTableBatch.QuestionName(Question, chunk[i].Ref);
                    questions.Add(new DecisionQuestion(name, DecisionQuestionKind.Choice,
                        "For the column " + JevTableBatch.Path("columns", i) + ": " + Instructions, options));
                    batch.Names[chunk[i]] = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(name, Question) };
                }
                batch.Questions = questions;
                batch.Snapshot = DecisionSnapshot.Build(point, new[]
                {
                    new KeyValuePair<string, object>("table_name", chunk[0].TableName),
                    new KeyValuePair<string, object>("table_columns", prepared.TableColumns[batch.TableRef]),
                    new KeyValuePair<string, object>("columns", chunk.Select(a => (object)a.Entry).ToList()),
                });
                return batch;
            }
            return JevTableBatch.Chunks(prepared.Asks, a => a.TableRef, size).SelectMany(c => JevTableBatch.Fit(c, Build)).ToList();
        }

        // ---- Jev's answer into a line -------------------------------------------------------------------------

        internal static void Map(Ask ask, DecisionAnswers answers, double bar, IReadOnlyList<FormatTemplateInfo> templates,
                                 List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused)
        {
            if (ask == null || answers == null || !answers.Decided) return;
            var answer = answers.For(Question);
            if (answer == null) return;
            Map(ask, answer.Choice, answer.Confidence, bar, templates, lines, refused);
        }

        /// <summary>One pick into one line, or a refusal. Pure: no model, no ask.</summary>
        internal static void Map(Ask ask, string family, double confidence, double bar, IReadOnlyList<FormatTemplateInfo> templates,
                                 List<ChangeItem> lines, List<ModelPlanChangeRefusal> refused)
        {
            if (ask == null || string.IsNullOrEmpty(family)) return;
            if (!DecisionRegistry.FormatFamilies.Contains(family)) return;           // never offered
            if (string.Equals(family, "text_or_none", StringComparison.Ordinal)) return;

            if (!OptionsFor(ask.DataType).Contains(family))
            {
                refused.Add(new ModelPlanChangeRefusal
                {
                    Target = ask.Ref,
                    Reason = ask.ColumnName + " holds " + (ask.DataType == nameof(DataType.DateTime) ? "dates" : "numbers")
                           + ", and the format chosen for it does not fit that, so it was left out.",
                });
                return;
            }

            string format, reason, check;
            if (string.Equals(family, "currency", StringComparison.Ordinal))
            {
                if (ask.MoneyFormat == null)
                {
                    refused.Add(new ModelPlanChangeRefusal
                    {
                        Target = ask.Ref,
                        Reason = ask.ColumnName + " looks like money, but this model has no money format yet, so no "
                               + "currency was picked for it. Choose one by hand.",
                    });
                    return;
                }
                format = ask.MoneyFormat;
                reason = ask.ColumnName + " looks like an amount of money, and " + format + " is the money format this model already uses.";
                check = format + " is already used " + JevFamily.Count(ask.MoneyFormatUses, "time", "times") + " in this model.";
            }
            else
            {
                var category = FamilyCategory[family];
                format = JevFamily.LibraryFormat(templates, family, category);
                if (format == null) return;
                reason = Reason(family, ask);
                check = format + " is the standard " + Lower(category) + " format in the format library, and it fits a "
                      + (ask.DataType == nameof(DataType.DateTime) ? "date" : "number") + " column.";
            }

            var problem = FormatStringRules.Problem(format);
            if (problem != null)
            {
                refused.Add(new ModelPlanChangeRefusal { Target = ask.Ref, Reason = problem });
                return;
            }

            var word = JevFamily.ConfidenceWord(confidence, bar);
            // THE FORMAT FILL RULE: only a line FormatFill proves may arrive approved. Anything else is a suggestion.
            var suggestion = !(ask.MayFillWholeNumber && FormatFill.IsWholeNumberFormat(format));
            lines.Add(new ChangeItem
            {
                ObjectRef = ask.Ref,
                ObjectName = ask.ColumnName,
                Kind = "set_format_string",
                Source = JevFamily.Source,
                Category = "Formatting",
                Severity = "Info",
                Risk = suggestion ? "suggestion" : "safe",
                Target = "formatString",
                Title = "Format as " + format,
                Rationale = JevFormatSuggestion.ReasonFor(suggestion, reason),
                Before = string.IsNullOrEmpty(ask.CurrentFormat) ? "(none)" : ask.CurrentFormat,
                After = format,
                Status = JevFormatSuggestion.Status(suggestion, string.Equals(word, JevFamily.Unsure, StringComparison.Ordinal), JevFamily.StatusFor(word)),
                JevItem = JevFamily.ItemId(Name, ask.TableRef),
                DecisionPoint = DecisionRegistry.ColumnFormat,
                DecisionConfidence = Math.Round(confidence, 4),
                DecisionWord = word,
                Reason = JevFormatSuggestion.ReasonFor(suggestion, reason),
                CheckText = suggestion ? JevFormatSuggestion.Note : check,
                WorkedOutDigest = ask.Digest,
            });
        }

        private static string Reason(string family, Ask ask)
        {
            switch (family)
            {
                case "percent": return ask.ColumnName + " looks like a share or a rate, so it shows out of 100.";
                case "whole_number": return ask.ColumnName + " counts whole things, so it shows no decimal places.";
                case "decimal": return ask.ColumnName + " is a measured amount that can have a fraction, so it shows two decimal places.";
                case "date_or_time": return ask.ColumnName + " holds dates, so it shows as a date.";
                default: return ask.ColumnName + " looks like a number of this kind, so it uses the matching format.";
            }
        }

        /// <summary>The first "why" line of a column format item.</summary>
        internal static string Missing(int n, string table) =>
            JevFamily.Count(n, "column", "columns") + " in " + table + (n == 1 ? " has" : " have")
            + " no format yet, so the same value reads differently depending on where it is shown.";

        private static string Lower(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);
    }
}
