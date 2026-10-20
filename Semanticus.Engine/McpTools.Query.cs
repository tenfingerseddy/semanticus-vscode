// The agent door's half of ask_data (Jev question-to-DAX, Kane 26 Sep). EngineRpcTarget.askData is the Studio's half;
// both land on IEngine.AskDataAsync, so a question asked from either door is read, checked and run the same way.
//
// Two things this door owes the assistant that the Studio's typed call does not need:
//   The description says only what this build does. Whether Jev reads words is Kane's call, held in one constant
//   (JevQueryReader.JevReads), so the sentence about it is chosen from that same constant at compile time.
//   The answer's `parts` can be handed straight back (`parts`), in the names they came in, and a name ask_data does not
//   take is refused in words rather than dropped: a dropped "groupBy" once turned "Net Sales by Region" into one total.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine.Query;

namespace Semanticus.Engine
{
    public static partial class McpTools
    {
        private const string AskDataWords = JevQueryReader.JevReads
            ? "Pass 'question' in plain words (\"net sales by store region for 2025, top 10 products\") and Jev reads which measures, columns, filters and top N it asks for from the model's names, column types, folders and descriptions; or pass the parts yourself"
            : "Reading a question in plain words is not switched on in this version: a 'question' alone comes back needs-you. Pass the parts yourself";

        private const string AskDataDescription =
            "Answer a question about the model's numbers with one fixed query shape (SUMMARIZECOLUMNS with TREATAS or KEEPFILTERS filters, and TOPN and ORDER BY), built, checked and run by Semanticus. "
            + AskDataWords
            + " (group_by, measures, filters, top, top_by, lowest, sort_by, descending), which are taken as said. To ask again with one part changed, pass the answer's 'parts' object back as 'parts' (either name style works) with the one part changed, or pass the snake_case parameters; a name ask_data does not take is refused, never ignored. "
            + "Filter values are matched on this computer against the connected model's values: pass the words (\"Coast\", \"2025\") and Semanticus finds the real value, or says none matches. Before running it checks that every column reaches every set of rows each measure counts through active relationships, that each filter value is really in the data and the filters can all hold, that a sort names a split column or a shown measure, and that the DAX checks out, and it caps the rows (max_rows, default 500). "
            + "Returns outcome (answered, built, needs-you, refused, error), a 'statement' (one plain sentence, the first thing to read, saying what the answer shows: \"This shows Net Sales for each Region, where Year is 2025.\"), a plain reading line such as \"Net Sales by Region, Year = 2025, top 10 by Net Sales\", who read it (jev, or you when the parts were given), the parts it settled, the DAX, each check, and the rows. A question in words is always answered when the model holds what it asks for: where Jev was not sure, or a part could not apply, the most likely reading runs and 'assumption' says in one plain line what was assumed (\"Using Net Sales. Metric Category is not filtered.\"), and 'others' holds each part changed back, with its whole 'parts', ready to pass back as 'parts'. Only a question the model does not hold comes back needs-you, naming the closest numbers in 'others'. When the run itself fails, outcome is error, the rows check says so in one plain line and 'error' carries the server's own words. When Jev was torn between two good readings of one part, the top one runs and 'others' names the other (\"Showing Net Sales. You may have meant Units.\"), with that reading's line and its whole 'parts', ready to pass back as 'parts' to run it instead. "
            + "Before a number is shown, a question read from words is checked once more: does the reading give the numbers the question asks for? When it does not (a change, a share, things with no sales, a forecast, a period it left out), the number still runs and 'assumption' says what is missing first. "
            + "Read-only; needs a connected model with data to match values and run. Returns rows, so the agent policy's QueryData rule applies to the connected target. Every ask is kept in the model's question history (list_data_questions), without its rows. Ask your data is a Semanticus Pro feature.";

        [McpServerTool(Name = "ask_data"), Description(AskDataDescription)]
        public static async Task<DataAnswer> AskData(IEngine engine,
            [Description("The question in plain words. Leave it out when you pass the parts")] string question = null,
            [Description("A previous answer's 'parts' object, passed back whole with any part changed. The parameters below, when given, override it")] JsonElement? parts = null,
            [Description("Columns to split by, e.g. [\"'Store'[Region]\"] or [\"column:Store/Region\"]")] string[] group_by = null,
            [Description("Measures to show, by name, e.g. [\"Net Sales\"]")] string[] measures = null,
            [Description("Filters as a JSON list of {\"column\": \"'Date'[Year]\", \"op\": \"is\", \"values\": [\"2025\"]}. op is is, is_not, after, from, before or up_to; values are words matched against the column's real values. A column of days takes after, from, before or up_to with one day written as 2025-12-31")] JsonElement? filters = null,
            [Description("Keep only the top N rows, ranked by top_by")] int? top = null,
            [Description("The measure that ranks the top rows (default: the first measure)")] string top_by = null,
            [Description("Rank from the smallest value (a bottom N)")] bool? lowest = null,
            [Description("A split column or a shown measure to sort by")] string sort_by = null,
            [Description("Sort from the largest or last value down")] bool? descending = null,
            [Description("The most rows to return (default 500, at most 10000)")] int? max_rows = null,
            [Description("False builds and checks the query without running it")] bool? run = null)
        {
            var ask = PartsFrom(parts) ?? new DataAsk();
            if (question != null) ask.Question = question;
            if (group_by != null) ask.GroupBy = group_by;
            if (measures != null) ask.Measures = measures;
            var f = FiltersFrom(filters);
            if (f != null) ask.Filters = f;
            if (top != null) ask.Top = top;
            if (top_by != null) ask.TopBy = top_by;
            if (lowest != null) ask.Lowest = lowest.Value;
            if (sort_by != null) ask.SortBy = sort_by;
            if (descending != null) ask.Descending = descending.Value;
            if (max_rows != null) ask.MaxRows = max_rows.Value;
            if (run != null) ask.Run = run.Value;
            var answer = await engine.AskDataAsync(ask, "agent").ConfigureAwait(false);
            // Golden rule 2: the UI door is pushed this live, so an open Ask your data page lists the question at once.
            // A read-only activity, not a model change, and no rows ride on it (the page reads the history, which keeps none).
            Emit(engine, new ActivityEvent { Kind = "ask_data", Origin = "agent", Label = "Asked a question about the data",
                Target = answer?.Reading ?? question, Ok = answer?.Outcome == DataAnswer.Answered || answer?.Outcome == DataAnswer.Built });
            return answer;
        }

        [McpServerTool(Name = "list_data_questions"), Description("Read the open model's question history for ask_data, newest first: each question's words, the reading line, who read it (jev or you), the outcome, the settled parts (pass them to ask_data as 'parts' to ask again or edit one part without Jev), the DAX, how many rows came back, when, and which door asked. Rows are never kept. Both the Studio and ask_data write the same history, kept beside the model like the Plan and never committed with it; a model opened live keeps its own, and an unsaved model keeps none. Read-only. Ask your data is a Semanticus Pro feature.")]
        public static Task<DataQuestionHistory> ListDataQuestions(IEngine engine,
            [Description("The most questions to return (default 50)")] int limit = 50)
            => engine.ListDataQuestionsAsync(limit);

        [McpServerTool(Name = "query_notebook"), Description("The Query page's notebook and its full history for the open model, the same rows the person sees. Each row is one question or one query run as written: its words or DAX, the reading, the number it showed (headline), how long it took, when, which door asked, and two flags: 'pinned' (stays at the top of the notebook) and 'inNotebook' (open in the notebook). The notebook is the rows that are pinned or open; the history is every row, newest first. 'action' is list (default), pin, unpin, close (the row leaves the notebook and stays in the history; a pinned row is unpinned too), reopen (a history row comes back into the notebook) or remove (delete the row for good). Every action but list needs 'id' from the list. Rows never carry data. Kept beside the model, never committed with it; an unsaved model keeps none. Free on every tier; it asks no one and sends nothing out. Use it to keep the person's notebook tidy or to find a query they asked before: reopen it, then show it with show_in_studio.")]
        public static Task<QueryNotebookView> QueryNotebook(IEngine engine,
            [Description("list (default), pin, unpin, close, reopen or remove")] string action = "list",
            [Description("The row's id, from the list. Required for every action but list")] string id = null)
            => engine.QueryNotebookAsync(action, id);

        [McpServerTool(Name = "pick_chart_kind"), Description("Pick the visual a Query result opens as: Jev's pick among the visuals the visual rules leave (queries.chart_kind). Pass the result's shape and the rules' own answer: 'fits' (the kinds the rules leave, in their order) and 'rules_pick'. Kinds are total, bars, line, variance, dumbbell, bullet, slope, multiples, sparktable and table. Jev's visual pick is off in this version until its replay clears its bar, so today this returns the rules' pick with by 'rules' and a note saying why; it does the same whenever Jev is not asked or not sure. Returns kind, by (jev or rules), a fixed reason sentence written by Semanticus, the confidence when Jev answered, and a note. Sends the question's words and the shape only, never rows or values. Read-only; never changes the model. Ask your data is a Semanticus Pro feature.")]
        public static Task<ChartPick> PickChartKind(IEngine engine,
            [Description("The result's shape as JSON: {\"dims\": [{\"name\": \"Region\", \"isDate\": false, \"distinct\": 4}], \"measures\": 1, \"rows\": 4}. Optional: \"measureNames\" (the display name of each number), \"sameUnit\" and \"negatives\" (yes or no facts)")] JsonElement shape,
            [Description("The visuals the visual rules leave for this shape, in the rules' order")] string[] fits,
            [Description("The visual the rules picked; the answer whenever Jev does not pick")] string rules_pick,
            [Description("The question in plain words, when there were any")] string words = null)
            => engine.PickChartKindAsync(new ChartPickAsk { Words = words, Shape = ShapeFrom(shape), Fits = fits, RulesPick = rules_pick });

        [McpServerTool(Name = "pick_check_visuals"), Description("Pick the visual each check on the Validation page opens with: Jev's pick among the visuals the page's rules leave for each check (checks.visual_kind), for every check in ONE call. Pass 'checks', each { id, title, measure, kindWord, status (pass, differs, off or could_not_check), rows, hasPrior, runs, hasSplit, fits (the visuals the rules leave, in their order), rulesPick }. Visuals are total, bars, line, variance, dumbbell, bullet, slope, multiples, waterfall, sparktable, heat and table. At most 40 checks. Jev's visual pick is off in this version until its replay clears its bar, so today this returns the rules' pick for every check with by 'rules' and a note saying why; it does the same whenever Jev is not asked or not sure. Returns picks, each { id, kind, by (jev or rules), a fixed reason sentence written by Semanticus, confidence when Jev answered, note }, and one note for the call. Sends check names, measure names, status words and counts only, never rows or values. Read-only; never changes the model. Ask your data is a Semanticus Pro feature.")]
        public static Task<CheckVisualPicks> PickCheckVisuals(IEngine engine,
            [Description("The checks to pick for: [{\"id\": \"gl\", \"title\": \"Total Sales and the GL\", \"measure\": \"Total Sales\", \"kindWord\": \"against SQL\", \"status\": \"differs\", \"rows\": 12, \"hasPrior\": true, \"runs\": 8, \"hasSplit\": true, \"fits\": [\"variance\", \"bars\", \"table\"], \"rulesPick\": \"variance\"}]")] CheckVisualItem[] checks)
            => engine.PickCheckVisualsAsync(new CheckVisualAsk { Checks = checks });

        private static ChartPickShape ShapeFrom(JsonElement v)
        {
            // Missing reads as no shape, so the engine's own gates (Pro first) answer before any shape error does.
            if (v.ValueKind == JsonValueKind.Undefined || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String)
            {
                using var doc = JsonDocument.Parse(v.GetString());
                return ShapeFrom(doc.RootElement.Clone());
            }
            if (v.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("shape must be a JSON object with dims, measures and rows.");
            var shape = new ChartPickShape { Dims = Array.Empty<ChartPickDim>() };
            foreach (var p in v.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "dims":
                        if (p.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException("shape.dims must be a list.");
                        shape.Dims = p.Value.EnumerateArray().Select(d =>
                        {
                            var dim = new ChartPickDim();
                            foreach (var q in d.EnumerateObject())
                            {
                                if (q.Name == "name") dim.Name = q.Value.GetString();
                                else if (q.Name == "isDate" || q.Name == "is_date") dim.IsDate = q.Value.GetBoolean();
                                else if (q.Name == "distinct") dim.Distinct = q.Value.GetInt32();
                                else throw new ArgumentException($"A split carries '{q.Name}', which a split does not take. It takes name, isDate and distinct.");
                            }
                            return dim;
                        }).ToArray();
                        break;
                    case "measures": shape.Measures = p.Value.GetInt32(); break;
                    case "rows": shape.Rows = p.Value.GetInt32(); break;
                    case "measureNames": case "measure_names":
                        if (p.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException("shape.measureNames must be a list of names.");
                        shape.MeasureNames = p.Value.EnumerateArray().Select(x => x.GetString()).ToArray();
                        break;
                    case "sameUnit": case "same_unit": shape.SameUnit = p.Value.GetBoolean(); break;
                    case "negatives": case "hasNegatives": case "has_negatives": shape.Negatives = p.Value.GetBoolean(); break;
                    default: throw new ArgumentException($"shape carries '{p.Name}', which it does not take. It takes dims, measures, rows, measureNames, sameUnit and negatives.");
                }
            }
            return shape;
        }

        /// <summary>The names ask_data takes as parameters, read off the method so this list cannot drift from it.</summary>
        internal static readonly HashSet<string> AskDataArguments = new HashSet<string>(
            typeof(McpTools).GetMethod(nameof(AskData)).GetParameters()
                .Where(p => p.ParameterType != typeof(IEngine) && p.ParameterType != typeof(System.Threading.CancellationToken))
                .Select(p => p.Name),
            StringComparer.Ordinal);

        /// <summary>A refusal naming the first argument ask_data does not take, or null. The SDK drops an unknown name
        /// without a word; McpDoorFilters asks this first so the assistant is told instead.</summary>
        internal static string UnknownAskDataArgument(IEnumerable<string> names)
        {
            var unknown = (names ?? Enumerable.Empty<string>()).FirstOrDefault(n => !AskDataArguments.Contains(n));
            if (unknown == null) return null;
            var hint = PartsKeys.TryGetValue(unknown, out var snake) && AskDataArguments.Contains(snake) ? $" Did you mean '{snake}'? Or pass the answer's parts whole as 'parts'." : "";
            return $"ask_data does not take '{unknown}'. It takes {string.Join(", ", AskDataArguments.OrderBy(x => x, StringComparer.Ordinal))}.{hint}";
        }

        /// <summary>The names a parts object may carry, in either style, to the snake_case parameter each one is.</summary>
        private static readonly Dictionary<string, string> PartsKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["question"] = "question",
            ["groupBy"] = "group_by", ["group_by"] = "group_by",
            ["measures"] = "measures",
            ["filters"] = "filters",
            ["top"] = "top",
            ["topBy"] = "top_by", ["top_by"] = "top_by",
            ["lowest"] = "lowest",
            ["sortBy"] = "sort_by", ["sort_by"] = "sort_by",
            ["descending"] = "descending",
            ["maxRows"] = "max_rows", ["max_rows"] = "max_rows",
            ["run"] = "run",
        };

        /// <summary>Read a parts object in either name style. A name it does not know is refused plainly.</summary>
        private static DataAsk PartsFrom(JsonElement? value)
        {
            if (value == null) return null;
            var v = value.Value;
            if (v.ValueKind == JsonValueKind.Undefined || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String)
            {
                using var doc = JsonDocument.Parse(v.GetString());
                return PartsFrom(doc.RootElement.Clone());
            }
            if (v.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("parts must be the JSON object a previous ask_data answer returned as 'parts'.");
            var ask = new DataAsk();
            foreach (var p in v.EnumerateObject())
            {
                if (!PartsKeys.TryGetValue(p.Name, out var name))
                    throw new ArgumentException($"parts carries '{p.Name}', which ask_data does not take. It takes {string.Join(", ", PartsKeys.Keys.OrderBy(x => x, StringComparer.Ordinal))}.");
                if (p.Value.ValueKind == JsonValueKind.Null) continue;
                switch (name)
                {
                    case "question": ask.Question = p.Value.GetString(); break;
                    case "group_by": ask.GroupBy = Strings(p.Value, p.Name); break;
                    case "measures": ask.Measures = Strings(p.Value, p.Name); break;
                    case "filters": ask.Filters = FiltersFrom(p.Value); break;
                    case "top": ask.Top = p.Value.GetInt32(); break;
                    case "top_by": ask.TopBy = p.Value.GetString(); break;
                    case "lowest": ask.Lowest = p.Value.GetBoolean(); break;
                    case "sort_by": ask.SortBy = p.Value.GetString(); break;
                    case "descending": ask.Descending = p.Value.GetBoolean(); break;
                    case "max_rows": ask.MaxRows = p.Value.GetInt32(); break;
                    case "run": ask.Run = p.Value.GetBoolean(); break;
                }
            }
            return ask;
        }

        private static string[] Strings(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()).ToArray()
            : e.ValueKind == JsonValueKind.String ? new[] { e.GetString() }
            : throw new ArgumentException($"parts '{name}' must be a list of names.");

        private static DataAskFilter[] FiltersFrom(JsonElement? value)
        {
            if (value == null) return null;
            var v = value.Value;
            if (v.ValueKind == JsonValueKind.Undefined || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String)
            {
                using var doc = JsonDocument.Parse(v.GetString());
                return FiltersFrom(doc.RootElement.Clone());
            }
            if (v.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("filters must be a JSON list of {\"column\", \"op\", \"values\"}. Check the action's description, then call again.");
            var list = new List<DataAskFilter>();
            foreach (var f in v.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Each filter must be a JSON object with column, op and values.");
                foreach (var p in f.EnumerateObject())
                    if (p.Name != "column" && p.Name != "op" && p.Name != "values" && p.Name != "value")
                        throw new ArgumentException($"A filter carries '{p.Name}', which a filter does not take. It takes column, op and values.");
                string Str(string name) => f.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                string[] values = Array.Empty<string>();
                if (f.TryGetProperty("values", out var vs))
                    values = vs.ValueKind == JsonValueKind.Array ? vs.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).ToArray()
                        : vs.ValueKind == JsonValueKind.String ? new[] { vs.GetString() } : new[] { vs.GetRawText() };
                else if (f.TryGetProperty("value", out var one))
                    values = new[] { one.ValueKind == JsonValueKind.String ? one.GetString() : one.GetRawText() };
                list.Add(new DataAskFilter { Column = Str("column"), Op = Str("op") ?? QueryOps.Is, Values = values });
            }
            return list.ToArray();
        }
    }
}
