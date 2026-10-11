// The one proof point for M0a: a display folder and a number format proposed for a new measure.
//
// It proposes. It never writes. Nothing in this file touches the model, and the operation that calls it
// returns the proposal beside the measure so a person or an assistant can take it or leave it.
//
// Criteria wording. Every criterion here describes THE WORLD, never the product's state and never the answer
// we are hoping for. "the measure is an amount of money" is a fact about the measure. "this is the folder
// most measures already use" is a fact about our own state, and feeding that back would make the service
// agree with us and call it evidence. The same rule is why the folder criteria are built from the folder's
// own NAME and nothing else, and why "none" is described as a property of the measure rather than as a
// fallback.
//
// The formula never leaves (F-229). A formula can carry a value, a customer's name in a filter or a price in
// a threshold, and the consent card promises that values never go. So the formula is read HERE, on this
// computer, and only facts about it are sent: which functions it uses (DAX function names, or the name of
// one of the model's own functions, which is a model name like any other), whether it divides, counts or sums
// an amount of money, whether it touches a date column, and whether it is a time comparison. Beside those
// facts go the names of the measures it uses, from the dependency list (references_measures): a money measure
// built as [Sales] - [Cost] calls no function at all, and the live replay lost 17 points on format without
// them. Names are inside the card's promise. No text, no literal, no column or table written in the formula.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;

namespace Semanticus.Engine.Decisions
{
    public static class MeasureDefaultsPoint
    {
        public const string FolderQuestion = "display_folder";
        public const string FormatQuestion = "format_family";

        /// <summary>The prefix every MODEL-DERIVED folder option id carries. It is what keeps the option ids
        /// apart from the labels they were built from: a folder literally named "none" used to produce the
        /// reserved no-folder id a second time, DecisionQuestion refused the duplicate, and the whole
        /// operation threw before it could answer. No reserved id carries this prefix, so no folder name can
        /// ever reach one. (Codex on PR 384.)</summary>
        public const string FolderIdPrefix = "folder:";

        /// <summary>The option id for one of the model's own folders. The folder NAME stays in the criterion,
        /// which is what the service reads; the id is only what comes back.</summary>
        public static string FolderOptionId(string folder) => FolderIdPrefix + (folder ?? string.Empty);

        /// <summary>How many sibling measures and columns go into the snapshot. A cap, not a sample: the point
        /// of a bound is that a 4000-column model costs the same as a 40-column one.</summary>
        private const int MaxSiblings = 40;
        private const int MaxColumns = 40;
        private const int MaxFolders = 30;

        /// <summary>The fixed six, with criteria that describe the numbers rather than the product.</summary>
        private static readonly Dictionary<string, string> FormatCriteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "currency",     "the number is an amount of money, such as a price, a cost, a total sold or a margin in cash" },
            { "percent",      "the number is a share of something, a rate or a growth figure, and reads naturally out of 100" },
            { "whole_number", "the number is a count of things that cannot be split, such as orders, people or items" },
            { "decimal",      "the number is a measured quantity that can have a fractional part, such as a weight, a distance, a ratio or an average that is not money and not a share" },
            { "date_or_time", "the number is a date, a time, or a length of time" },
            { "text_or_none", "the result is text, or it is a number that no standard format fits" },
        };

        /// <summary>Whether this measure is one the point may be asked about at all: created without a display
        /// folder and without a format string. A measure that already carries either is left alone, because the
        /// person or the rule that set it has said what they wanted. A measure shown through a format string expression
        /// HAS a format, though its static one is blank: a static format beside it is never proposed (the format fill
        /// rule, FormatFill.HasNoFormat), because approving one would set both.</summary>
        public static bool WantsProposal(Measure measure, out bool wantsFolder, out bool wantsFormat)
        {
            wantsFolder = measure != null && string.IsNullOrWhiteSpace(measure.DisplayFolder);
            // No line is offered over text a person typed (even blank spaces or a no-break space): it is a value, not empty, and a tick
            // given on a page that could not show it must never write over it (addendum 3 item 18).
            wantsFormat = measure != null && Semanticus.Analysis.FormatFill.HasNoFormat(measure) && Semanticus.Analysis.FormatFill.HoldsNoFormatText(measure);
            return wantsFolder || wantsFormat;
        }

        /// <summary>The declared snapshot, built through <see cref="DecisionSnapshot"/> so an undeclared field is
        /// impossible rather than merely unlikely. Names, types and facts worked out from the formula: no formula
        /// text, no rows, no values, no endpoints, no workspace or tenant, no credentials.</summary>
        public static IReadOnlyDictionary<string, object> Snapshot(Measure measure)
        {
            if (measure == null) throw new ArgumentNullException(nameof(measure));
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            var table = measure.Table;

            var siblings = SiblingMeasures(table, measure);
            var columns = TableColumns(table);

            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("measure_name", measure.Name),
                new KeyValuePair<string, object>("measure_formula_facts", FormulaFacts(measure)),
                new KeyValuePair<string, object>("references_measures", ReferencedMeasures(measure)),
                new KeyValuePair<string, object>("table_name", table.Name),
                new KeyValuePair<string, object>("table_columns", columns),
                new KeyValuePair<string, object>("sibling_measures", siblings),
            });
        }

        /// <summary>The names, folders and formats of a table's other measures (the asked one left out), the first forty by name.</summary>
        private static List<object> SiblingMeasures(Table table, Measure except) =>
            table.Measures
                .Where(m => except == null || !ReferenceEquals(m, except))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSiblings)
                .Select(m => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", m.Name },
                    { "display_folder", string.IsNullOrWhiteSpace(m.DisplayFolder) ? null : m.DisplayFolder },
                    { "format_string", string.IsNullOrWhiteSpace(m.FormatString) ? null : m.FormatString },
                })
                .ToList();

        /// <summary>The names and types of a table's columns, the first forty by name.</summary>
        private static List<object> TableColumns(Table table) =>
            table.Columns
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxColumns)
                .Select(c => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", c.Name },
                    { "data_type", c.DataType.ToString() },
                })
                .ToList();

        /// <summary>What every measure asked about in one table shares in a BATCHED request (JevTableBatch.cs): the table's
        /// columns and ALL its measures' names, folders and formats (the asked ones included, which the per-measure request
        /// left out of its own siblings).</summary>
        public sealed class TableShare
        {
            public string TableName;
            public List<object> TableColumns;
            public List<object> SiblingMeasures;
        }

        public static TableShare Share(Table table) => new TableShare
        {
            TableName = table.Name,
            TableColumns = TableColumns(table),
            SiblingMeasures = SiblingMeasures(table, null),
        };

        /// <summary>One measure's entry in a batched request's `measures`: its name, the facts worked out from its formula, and
        /// the names of the measures it uses. Never the formula.</summary>
        public static Dictionary<string, object> Entry(Measure measure) => new Dictionary<string, object>(StringComparer.Ordinal)
        {
            { "name", measure.Name },
            { "formula_facts", FormulaFacts(measure) },
            { "references_measures", ReferencedMeasures(measure) },
        };

        /// <summary>The declared snapshot of one chunk of one table's measures.</summary>
        public static IReadOnlyDictionary<string, object> BatchSnapshot(TableShare share, IEnumerable<Dictionary<string, object>> entries)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("table_name", share.TableName),
                new KeyValuePair<string, object>("table_columns", share.TableColumns),
                new KeyValuePair<string, object>("sibling_measures", share.SiblingMeasures),
                new KeyValuePair<string, object>("measures", entries.Cast<object>().ToList()),
            });
        }

        // ---- facts about the formula, worked out here -------------------------------------------------------

        private static readonly HashSet<string> CountFunctions = new HashSet<string>(StringComparer.Ordinal)
        {
            "COUNT", "COUNTA", "COUNTAX", "COUNTX", "COUNTROWS", "COUNTBLANK",
            "DISTINCTCOUNT", "DISTINCTCOUNTNOBLANK", "APPROXIMATEDISTINCTCOUNT",
        };

        private static readonly HashSet<string> SumFunctions = new HashSet<string>(StringComparer.Ordinal)
        {
            "SUM", "SUMX",
        };

        /// <summary>The standard time-intelligence family: functions that shift, accumulate or bound a period
        /// of a calendar. Using any of them is a fact about what the measure means.</summary>
        private static readonly HashSet<string> TimeIntelligenceFunctions = new HashSet<string>(StringComparer.Ordinal)
        {
            "SAMEPERIODLASTYEAR", "PARALLELPERIOD", "DATEADD", "DATESBETWEEN", "DATESINPERIOD",
            "TOTALYTD", "TOTALQTD", "TOTALMTD", "DATESYTD", "DATESQTD", "DATESMTD",
            "PREVIOUSYEAR", "PREVIOUSQUARTER", "PREVIOUSMONTH", "PREVIOUSDAY",
            "NEXTYEAR", "NEXTQUARTER", "NEXTMONTH", "NEXTDAY",
            "STARTOFYEAR", "STARTOFQUARTER", "STARTOFMONTH", "ENDOFYEAR", "ENDOFQUARTER", "ENDOFMONTH",
            "OPENINGBALANCEYEAR", "OPENINGBALANCEQUARTER", "OPENINGBALANCEMONTH",
            "CLOSINGBALANCEYEAR", "CLOSINGBALANCEQUARTER", "CLOSINGBALANCEMONTH",
            "FIRSTDATE", "LASTDATE",
        };

        /// <summary>Words that make a column's own NAME read as an amount of money. The name is already sent
        /// with the table's columns, so this adds a judgement, not a detail.</summary>
        private static readonly string[] MoneyWords =
        {
            "amount", "price", "cost", "revenue", "sales", "profit", "margin", "freight", "tax", "discount",
            "fee", "spend", "budget", "income", "expense", "salary", "value",
        };

        /// <summary>Facts about the measure's formula, worked out on this computer. This is what the point sends
        /// in place of the formula (F-229), and every entry is either a name from the DAX function vocabulary or
        /// a yes or no:
        ///
        ///   functions                  the functions the formula calls, in the order it first calls them, so the
        ///                              outer function leads. A function is a token the DAX lexer knows as a
        ///                              keyword, or a bare name, followed by an opening bracket; a string, a
        ///                              number, a column or a table can never be one.
        ///   returns_ratio              it divides: DIVIDE, or the / operator
        ///   returns_count              it counts (COUNT and kin, DISTINCTCOUNT) and does not divide
        ///   returns_currency_like_sum  it sums (SUM, SUMX) a column that holds money, and does not divide
        ///   references_date_column     it reads a date column, or a column of the table marked as the date table
        ///   time_intelligence          it calls a time-intelligence function (SAMEPERIODLASTYEAR, TOTALYTD, ...)
        ///
        /// A column holds money when its type is fixed decimal (the type TOM calls currency), its format string
        /// shows a currency sign, or its own name reads as money. The columns come from the model's dependency
        /// list for this measure, so no name is read out of the formula text.</summary>
        public static IReadOnlyDictionary<string, object> FormulaFacts(Measure measure)
        {
            if (measure == null) throw new ArgumentNullException(nameof(measure));

            var tokens = DaxDependencyHelper.Tokenize(measure.Expression ?? string.Empty, includeHidden: false);
            var functions = new List<string>();
            var divides = false;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Type == DaxToken.DIV) { divides = true; continue; }
                if (i + 1 >= tokens.Count || tokens[i + 1].Type != DaxToken.OPEN_PARENS) continue;
                if (!IsFunctionToken(token)) continue;
                var name = token.Text.ToUpperInvariant();
                if (!functions.Contains(name)) functions.Add(name);
            }
            divides |= functions.Contains("DIVIDE");

            IReadOnlyList<Column> columns;
            try { columns = measure.DependsOn.Columns.Distinct().ToList(); }
            catch { columns = Array.Empty<Column>(); }

            var counts = functions.Any(CountFunctions.Contains);
            var sumsMoney = functions.Any(SumFunctions.Contains) && columns.Any(HoldsMoney);

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "functions", functions.Cast<object>().ToList() },
                { "returns_ratio", divides },
                { "returns_count", counts && !divides },
                { "returns_currency_like_sum", sumsMoney && !divides },
                { "references_date_column", columns.Any(IsDateColumn) },
                { "time_intelligence", functions.Any(TimeIntelligenceFunctions.Contains) },
            };
        }

        /// <summary>The names of the measures this measure's formula uses, from the model's dependency list and
        /// never from the text, sorted so the same set is the same evidence whatever order the formula uses.</summary>
        public static IReadOnlyList<object> ReferencedMeasures(Measure measure)
        {
            if (measure == null) throw new ArgumentNullException(nameof(measure));
            try
            {
                return measure.DependsOn.Measures.Select(m => m.Name).Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal).Cast<object>().ToList();
            }
            catch { return Array.Empty<object>(); }
        }

        /// <summary>A token that can name a function: one the lexer files on its keyword channel (the DAX
        /// function vocabulary), or a bare name, which is how a function newer than the lexer, or a model's own
        /// function, arrives. A bare name directly before a bracket is always a function call in DAX: a table or
        /// a variable is never called.</summary>
        private static bool IsFunctionToken(DaxToken token)
        {
            if (token.Type == DaxToken.TABLE_OR_VARIABLE) return true;
            if (token.Type == DaxToken.IN) return false;   // x IN ( ... ) is an operator, not a call
            var symbol = TabularEditor.TextServices.DAXLexer.DefaultVocabulary.GetSymbolicName(token.Type);
            if (string.IsNullOrEmpty(symbol)) return false;
            // Every keyword-channel token in the grammar is declared before the literals and punctuation, and
            // its symbolic name is its own spelling with the dots removed. Checking the spelling rather than
            // an index keeps this honest if the grammar is regenerated.
            return string.Equals(symbol, token.Text.Replace(".", string.Empty), StringComparison.OrdinalIgnoreCase);
        }

        private static bool HoldsMoney(Column column)
        {
            if (column == null) return false;
            if (column.DataType == DataType.Decimal) return true;
            var format = column.FormatString ?? string.Empty;
            if (format.IndexOfAny(new[] { '$', '\u00a3', '\u20ac', '\u00a5' }) >= 0
                || format.IndexOf("currency", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            var name = (column.Name ?? string.Empty).ToLowerInvariant();
            return MoneyWords.Any(w => name.Contains(w, StringComparison.Ordinal));
        }

        private static bool IsDateColumn(Column column)
        {
            if (column == null) return false;
            if (column.DataType == DataType.DateTime) return true;
            return string.Equals(column.Table?.DataCategory, "Time", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The folders to offer, most relevant first, and how many the model has in all. Relevance, not the alphabet
        /// (m11/jev-batch R3: the first thirty by name left the folder this table's own measures already sit in out whenever
        /// it sorted late): a folder this table already uses first, then the folders more of the model uses, then the
        /// order the caller listed them in (its own folders first), then the name. Only the cap cuts, and
        /// <c>Total</c> says how many there were, so a caller can tell the person some were not offered.</summary>
        public static (IReadOnlyList<string> Offered, int Total) RankFolders(IEnumerable<string> existingFolders,
            IReadOnlyDictionary<string, int> uses = null, ISet<string> onThisTable = null)
        {
            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in existingFolders ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                var name = f.Trim();
                if (seen.Add(name)) distinct.Add(name);
            }
            int UsesOf(string f) => uses != null && uses.TryGetValue(f, out var n) ? n : 0;
            var ranked = distinct
                .Select((f, position) => (Folder: f, Position: position))
                .OrderBy(x => onThisTable != null && onThisTable.Contains(x.Folder) ? 0 : 1)
                .ThenByDescending(x => UsesOf(x.Folder))
                .ThenBy(x => x.Position)
                .ThenBy(x => x.Folder, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Folder)
                .ToList();
            return (ranked.Take(MaxFolders).ToList(), ranked.Count);
        }

        /// <summary>The most folders one question offers (the rest are not offered, and the line says so).</summary>
        public static int FolderCap => MaxFolders;

        /// <summary>How many measures and columns use each display folder in the whole model, by trimmed name. What "more of
        /// the model uses" means in <see cref="RankFolders"/>.</summary>
        public static Dictionary<string, int> FolderUses(Model m)
        {
            var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in m.Tables)
                foreach (var f in t.Measures.Select(x => x.DisplayFolder).Concat(t.Columns.Select(x => x.DisplayFolder)))
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    var key = f.Trim();
                    uses[key] = uses.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            return uses;
        }

        /// <summary>The display folders this table's own measures, columns and hierarchies use.</summary>
        public static HashSet<string> FoldersOf(Table t) =>
            new HashSet<string>(t.Measures.Select(x => x.DisplayFolder).Concat(t.Columns.Select(x => x.DisplayFolder))
                .Concat(t.Hierarchies.Select(x => x.DisplayFolder))
                .Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()), StringComparer.OrdinalIgnoreCase);

        /// <summary>The questions, with the folder options taken from the folders this model already uses plus
        /// "none". Returns an empty list when there is nothing left to ask. <paramref name="uses"/> and
        /// <paramref name="onThisTable"/> rank the folders (<see cref="RankFolders"/>); without them the caller's order
        /// stands.</summary>
        public static IReadOnlyList<DecisionQuestion> Questions(IEnumerable<string> existingFolders, bool wantsFolder, bool wantsFormat,
            IReadOnlyDictionary<string, int> uses = null, ISet<string> onThisTable = null)
        {
            var questions = new List<DecisionQuestion>();

            if (wantsFolder)
            {
                var folders = RankFolders(existingFolders, uses, onThisTable).Offered;

                // With no folders in the model there is no choice to make: "none" against itself is not a
                // question, and asking it would spend money to be told what we already know.
                if (folders.Count > 0)
                {
                    var options = folders
                        .Select(f => new DecisionOption(FolderOptionId(f), $"the measure is about {f}"))
                        .ToList();
                    options.Add(new DecisionOption(DecisionRegistry.NoFolder,
                        "the measure does not belong with any of the groups above, or it is general enough that grouping it would hide it"));

                    questions.Add(new DecisionQuestion(FolderQuestion, DecisionQuestionKind.Choice,
                        "Which group does this measure belong in?", options));
                }
            }

            if (wantsFormat)
            {
                var options = DecisionRegistry.FormatFamilies
                    .Select(f => new DecisionOption(f, FormatCriteria[f]))
                    .ToList();
                questions.Add(new DecisionQuestion(FormatQuestion, DecisionQuestionKind.Choice,
                    "What kind of number does this measure produce?", options));
            }

            return questions;
        }

        /// <summary>Read a folder proposal out of an answer set, but only above the bar. "none" is a real answer
        /// and means no folder is proposed, which is different from being below the bar; both come back as null
        /// here, and the caller reports the confidence either way. The answer carries an option ID, so the
        /// folder NAME is decoded back out of it; anything that is not a model-derived id, the reserved
        /// no-folder id included, means no folder.</summary>
        public static string ProposedFolder(DecisionAnswers answers, double bar, out double confidence)
        {
            confidence = 0;
            var answer = answers?.For(FolderQuestion);
            if (answer == null) return null;
            confidence = answer.Confidence;
            if (answer.Confidence < bar) return null;
            return FolderFromOptionId(answer.Choice);
        }

        /// <summary>The folder name behind a model-derived option id, or null when the id is not one.</summary>
        public static string FolderFromOptionId(string optionId)
        {
            if (optionId == null || !optionId.StartsWith(FolderIdPrefix, StringComparison.Ordinal)) return null;
            var name = optionId.Substring(FolderIdPrefix.Length);
            return name.Length == 0 ? null : name;
        }

        public static string ProposedFormatFamily(DecisionAnswers answers, double bar, out double confidence)
        {
            confidence = 0;
            var answer = answers?.For(FormatQuestion);
            if (answer == null) return null;
            confidence = answer.Confidence;
            return answer.Confidence < bar ? null : answer.Choice;
        }
    }

    /// <summary>THE FORMAT FILL RULE ON A JEV FORMAT LINE (both format families: measures and columns). A Jev format line
    /// is an automatic write like any other, so it asks <c>FormatFill.MayFill</c>: a line the engine proves may arrive
    /// approved, as before. A line it cannot prove arrives as a SUGGESTION, and the words and the status for that live
    /// here so the two families cannot say them differently.</summary>
    public static class JevFormatSuggestion
    {
        /// <summary>The plain note a suggested line carries, in the check slot the person reads beside the line.</summary>
        public const string Note = "Jev suggests this format; check it fits before you run it.";

        /// <summary>The note on a line whose tick no longer holds because the measure or column it was ticked for is not the same
        /// object (its formula or type changed, or another object took the name). It goes back to a suggestion, unticked.</summary>
        public const string KeepVoidNote = "You ticked an earlier version of this line, and this measure or column has changed since. Jev suggests this format; check it fits before you run it.";

        /// <summary>The note on a line whose tick did not carry because the measure or column was renamed: a tick goes with the object and
        /// its name, so the person is told and asked to tick it again (addendum 4 item 28, L2).</summary>
        /// <summary>The note on a line whose tick was set aside because the same step changed a data type: the column's own, or
        /// that of a column the measure reads (v-rules-v1i, decision 4). The object did not become something else, so the note
        /// never says it "has changed since"; it says what happened, and the person ticks it again if it still fits.</summary>
        public const string TypeChangedKeepNote = "That step also changed a data type this format depends on, so your tick was set aside. Jev suggests this format; tick it again if it still fits.";

        public const string RenamedKeepNote = "You ticked an earlier version of this line, when this measure or column had another name, so your tick no longer counts. Jev suggests this format; check it fits before you run it.";

        /// <summary>The note on a line Jev worked out for the measure or column as it was: the object, or something it reads, has changed
        /// since and Jev has not answered again, so the old reading is not shown as current and a tick on it is refused (addendum 4 item 23).</summary>
        public const string StaleNote = "Jev worked this format out before this measure or column, or something it reads, changed, and has not answered again, so it cannot be ticked yet. Set the format yourself, or wait for Jev.";

        /// <summary>The note on a line the engine approved while its rule proved it, when the model has changed and Jev has not answered
        /// again: the rule no longer proves it, so the approval did not stay.</summary>
        public const string SilentNote = "Semanticus could no longer prove this format fits after the model changed, and Jev has not answered again. Check it fits before you run it.";

        /// <summary>The line's status. A suggestion is "proposed": not approved, so Run the plan never applies it, and a
        /// person keeps (ticks) it after checking. An unsure line stays dropped ("rejected"); a proved line is approved.
        /// <paramref name="statusForWord"/> is the family's ordinary status for the confidence word.</summary>
        public static string Status(bool suggestion, bool unsure, string statusForWord) =>
            suggestion && !unsure ? "proposed" : statusForWord;

        /// <summary>The "why" on a line. A proved line keeps the family's sentence, which states what the engine checked. A
        /// suggestion carries the same sentence as Jev's reading and says so: "it counts whole things" written as fact on a
        /// ratio is the premise Semanticus could not prove.</summary>
        public static string ReasonFor(bool suggestion, string reason) =>
            !suggestion || string.IsNullOrWhiteSpace(reason) ? reason
            : "Jev's reading, not proved: " + char.ToLowerInvariant(reason[0]) + reason.Substring(1);

        /// <summary>A line that is only a suggestion (a Jev line carrying the note).</summary>
        public static bool IsSuggestion(string checkText) =>
            string.Equals(checkText, Note, StringComparison.Ordinal) || string.Equals(checkText, KeepVoidNote, StringComparison.Ordinal)
            || string.Equals(checkText, SilentNote, StringComparison.Ordinal) || string.Equals(checkText, RenamedKeepNote, StringComparison.Ordinal)
            || string.Equals(checkText, StaleNote, StringComparison.Ordinal) || string.Equals(checkText, TypeChangedKeepNote, StringComparison.Ordinal);

        /// <summary>True when the lines of one Jev row hold suggestions and nothing approved: the row waits on a person.</summary>
        public static bool WaitsOnPerson(IReadOnlyCollection<(string status, string checkText)> lines) =>
            lines.Any(l => IsSuggestion(l.checkText) && !string.Equals(l.status, "approved", StringComparison.OrdinalIgnoreCase))
            && !lines.Any(l => string.Equals(l.status, "approved", StringComparison.OrdinalIgnoreCase));

        /// <summary>The "why" sentence for a row that carries suggestions, or null.</summary>
        public static string Why(IReadOnlyCollection<(string status, string checkText)> lines)
        {
            var n = lines.Count(l => IsSuggestion(l.checkText) && !string.Equals(l.status, "approved", StringComparison.OrdinalIgnoreCase));
            if (n == 0) return null;
            return n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                 + (n == 1 ? " format is only a suggestion, because Semanticus cannot prove it fits what the number is. Check it, then keep it if it fits."
                           : " formats are only suggestions, because Semanticus cannot prove they fit what the numbers are. Check each one, then keep the ones that fit.")
                 + " Run the plan never applies a suggestion.";
        }
    }
}
