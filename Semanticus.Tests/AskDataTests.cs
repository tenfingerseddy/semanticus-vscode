// ask_data (Jev question-to-DAX, Kane 26 Sep): the one query shape, the checks before it runs, values matched on
// this computer, Pro only, and the question history.
//
// The model is Contoso-style and built in code: Sales with two measures, Store, Product and Date on active
// relationships, Customer on an INACTIVE one, and Budget, which only Date reaches. A fake query connection answers the
// engine's own DISTINCT reads from in-memory values and records every query, so a test can prove what ran and what
// did not. Jev is a scripted oracle that records every request it is sent.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]   // the agent door's data gate writes the approval ledger: keep it in the scratch root
    public sealed partial class AskDataTests : IDisposable, IClassFixture<ModelTemplates>
    {
        // A policy and a ledger of this class's own. The collection's shared root is not enough: another class in it
        // may leave a preset or a grant behind (the full suite found one), and the agent door's refusal is asserted here.
        private readonly string _safeRoot;
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem-askdata-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        // The Contoso model is built once per shape and each rig opens a copy (lean suite, lane A). Tests in a class run one after
        // another, so one static handle, set by the constructor, is enough for the static BuildAsync.
        private static ModelTemplates _templates;

        public AskDataTests(RestoreRootFixture fixture, ModelTemplates templates)
        {
            _templates = templates;
            _safeRoot = fixture.Root;
            Semanticus.Engine.AgentPolicyStore.RootOverride = _root;
            Semanticus.Engine.ApprovalLedger.RootOverride = _root;
        }

        public void Dispose()
        {
            Semanticus.Engine.AgentPolicyStore.RootOverride = _safeRoot;
            Semanticus.Engine.ApprovalLedger.RootOverride = _safeRoot;
            try { System.IO.Directory.Delete(_root, true); } catch { }
        }

        // ---- the data behind the fake connection --------------------------------------------------------------

        /// <summary>Values the question never names as they are spelled. None of them may ever reach Jev: not even
        /// "Harborview", which the question's "Harbourview" matches with a slip of the fingers.</summary>
        private static readonly string[] Sentinels = { "Harborview", "Lakeside", "Pinecrest", "Brightwater" };

        private static readonly Dictionary<string, object[]> Values = new Dictionary<string, object[]>(StringComparer.Ordinal)
        {
            ["Store[Region]"] = new object[] { "Coast", "North", "South", "Mountain" },
            ["Store[City]"] = Sentinels.Cast<object>().ToArray(),
            ["Product[Category]"] = new object[] { "Outdoor", "Kitchen", "Apparel" },
            ["Product[ProductName]"] = new object[] { "Trail Tent", "Camp Stove", "Rain Jacket" },
            ["Date[Year]"] = new object[] { 2024L, 2025L },
            ["Date[MonthName]"] = new object[] { "January", "February", "March", "December" },
            ["Customer[Segment]"] = new object[] { "Retail", "Trade" },
            // Read only by the rigs that add these columns (twins, calendar); the others never name them.
            ["Customer[Region]"] = new object[] { "Coast", "North" },
            ["Date[YearMonth]"] = new object[] { "2024-11", "2024-12", "2025-11", "2025-12" },
            // Read only by the rig with a metric category (K16): values that name numbers.
            ["Metric Category[Metric Category]"] = new object[] { "Sales", "Margin", "Cost" },
            ["Product[Brand]"] = new object[] { "Contoso", "Fabrikam", "Litware" },
        };

        private sealed class FakeData
        {
            public readonly List<string> Queries = new List<string>();
            public int RowsToReturn = 3;
            /// <summary>Values a test gives a column in place of the shared ones, by Table[Column] (a date table of another shape).</summary>
            public readonly Dictionary<string, object[]> Over = new Dictionary<string, object[]>(StringComparer.Ordinal);
            /// <summary>When set, the run itself (the SUMMARIZECOLUMNS query) fails with these words, as a server does.</summary>
            public string FailWith;

            public IEnumerable<string> Runs => Queries.Where(q => q.Contains("SUMMARIZECOLUMNS"));

            /// <summary>The measures the CONNECTED model holds, as its metadata says (name to formula and format). BuildAsync fills it from the
            /// model as built, so a test that edits the open copy afterwards has a connected copy that still holds the published formulas. Null
            /// is an endpoint that will not say (a reader without the rights).</summary>
            public Dictionary<string, (string Expression, string Format)> Connected;

            /// <summary>The last day the calendar rig's data holds a number on, for the engine's latest-date read.</summary>
            public DateTime Latest = new DateTime(2025, 12, 31);

            public ResultSet Execute(string q)
            {
                lock (Queries) Queries.Add(q);
                if (q.Contains("TMSCHEMA_MEASURES"))
                {
                    if (Connected == null) return ResultSet.FromError("The user does not have permission to read this metadata.");
                    var rows = Connected.Select(kv => new object[] { kv.Key, kv.Value.Expression, kv.Value.Format }).ToArray();
                    return new ResultSet { Columns = new[] { new ColumnDef { Name = "Name" }, new ColumnDef { Name = "Expression" }, new ColumnDef { Name = "FormatString" } }, Rows = rows, RowCount = rows.Length };
                }
                if (q.Contains("MAXX ( FILTER ( VALUES ( "))
                    return new ResultSet { Columns = new[] { new ColumnDef { Name = "[Latest]" } }, Rows = new[] { new object[] { Latest } }, RowCount = 1 };
                var on = Regex.Matches(q, @"DATE \( (\d+), (\d+), (\d+) \)");
                if (q.Contains("'Date'[Date] IN {") && on.Count > 0)
                {
                    // The calendar's own labels for each day asked about: Year, then YearMonth, as the rig's Date table holds them.
                    var days = on.Cast<Match>().Select(m => new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))).ToList();
                    var asked = Regex.Matches(q.Substring(q.IndexOf("ALL (", StringComparison.Ordinal)), @"'Date'\[([^\]]+)\]").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
                    object Label(DateTime d, string col) => col switch
                    {
                        "Date" => d, "Year" => (object)(long)d.Year, "YearMonth" => d.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                        "MonthName" => d.ToString("MMMM", CultureInfo.InvariantCulture), _ => null,
                    };
                    var rows = days.Select(d => asked.Select(c => Label(d, c)).ToArray()).ToArray();
                    return new ResultSet { Columns = asked.Select(c => new ColumnDef { Name = "Date[" + c + "]" }).ToArray(), Rows = rows, RowCount = rows.Length };
                }
                var d = Regex.Match(q, @"DISTINCT \( '((?:[^']|'')+)'\[((?:[^\]]|\]\])+)\] \)");
                if (d.Success)
                {
                    var key = d.Groups[1].Value.Replace("''", "'") + "[" + d.Groups[2].Value.Replace("]]", "]") + "]";
                    var vals = Over.TryGetValue(key, out var o) ? o : Values.TryGetValue(key, out var v) ? v : Array.Empty<object>();
                    return new ResultSet { Columns = new[] { new ColumnDef { Name = key } }, Rows = vals.Select(x => new[] { x }).ToArray(), RowCount = vals.Length };
                }
                if (q.Contains("SUMMARIZECOLUMNS") && FailWith != null) return ResultSet.FromError(FailWith);
                if (q.Contains("SUMMARIZECOLUMNS"))
                {
                    var measures = Regex.Matches(q, "\"@((?:[^\"]|\"\")+)\"").Count;
                    var groups = Regex.Matches(q.Substring(q.IndexOf("SUMMARIZECOLUMNS", StringComparison.Ordinal)), @"^\s+'[^']+'\[[^\]]+\],?$", RegexOptions.Multiline).Count;
                    var cols = Enumerable.Range(0, groups).Select(i => new ColumnDef { Name = "g" + i })
                        .Concat(Enumerable.Range(0, measures).Select(i => new ColumnDef { Name = "[@m" + i + "]" })).ToArray();
                    var rows = Enumerable.Range(0, RowsToReturn).Select(r => cols.Select((c, i) => (object)(i < groups ? "k" + r : (double)(100 - r))).ToArray()).ToArray();
                    return new ResultSet { Columns = cols, Rows = rows, RowCount = rows.Length };
                }
                return ResultSet.FromError("Unexpected query in the ask_data fixture: " + q);
            }
        }

        // ---- Jev, scripted -------------------------------------------------------------------------------------

        /// <summary>Answers query.summarize from a script over (request, question). A question the script does not name
        /// is a sure "none" (or a sure yes for q:fits). The check questions (q:check:gap, and q:check:real for a second opinion) are answered,
        /// in this order: by the script when it names them; by the recorded live check for this exact request
        /// (CheckRecordings; a test can only be answered from a recording its own request digest is in); else "nothing
        /// missing, yes" (the plumbing tests that are not about the check).</summary>
        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<DecisionRequest, DecisionQuestion, (string Choice, double P)?> _script;
            /// <summary>The probability Jev gives each option, when a test needs more than the pick and its confidence.</summary>
            public Func<DecisionRequest, DecisionQuestion, IReadOnlyDictionary<string, double>> Probabilities;
            /// <summary>The calls that read the parts (the first, the split, the table). The check calls are kept apart, in
            /// Checks, so a test that counts the parts calls is not disturbed by the check that comes after them.</summary>
            public readonly List<DecisionRequest> Seen = new List<DecisionRequest>();
            private readonly List<DecisionRequest> _checks = new List<DecisionRequest>();
            /// <summary>The check calls only (the requests holding q:check:gap), in order.</summary>
            public IReadOnlyList<DecisionRequest> Checks { get { lock (Seen) return _checks.ToList(); } }
            /// <summary>Every call, parts and checks, in the order they were made.</summary>
            public IReadOnlyList<DecisionRequest> All { get { lock (Seen) return Seen.Concat(_checks).ToList(); } }
            /// <summary>Every check answered from the recorded live run (a digest the recording holds).</summary>
            public readonly List<string> Recorded = new List<string>();
            /// <summary>The check calls come back not decided, as when Jev was not reachable.</summary>
            public bool NotDecideChecks;
            /// <summary>Only the second opinions come back not decided.</summary>
            public bool NotDecideSecondOpinions;
            /// <summary>Each check call waits this long (a test of the second opinions running side by side), and the most calls
            /// that were in flight at once are counted.</summary>
            public TimeSpan CheckDelay;
            public int MaxCheckCallsInFlight;
            private int _inFlight;
            /// <summary>Set by the test that named this rig, for the recording's item id.</summary>
            public string Test;
            public ScriptedOracle(Func<DecisionRequest, DecisionQuestion, (string, double)?> script) { _script = script; }
            public bool Available => true;

            internal static bool IsCheck(DecisionRequest r) => r.Questions.Any(q => q.Name == JevQueryReader.CheckGapQ || q.Name == JevQueryReader.CheckRealQ);

            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                var isCheck = IsCheck(request);
                lock (Seen) { if (isCheck) _checks.Add(request); else Seen.Add(request); }
                if (request.PointName != DecisionRegistry.QuerySummarize)
                    return DecisionAnswers.NotDecided(request, "this test answers query.summarize only");
                if (request.Questions.Any(q => q.Name == JevQueryReader.CheckRealQ) && NotDecideSecondOpinions) return DecisionAnswers.NotDecided(request, "the test made the second opinion unavailable");
                if (isCheck && NotDecideChecks) return DecisionAnswers.NotDecided(request, "the test made the check unavailable");
                if (isCheck && CheckDelay > TimeSpan.Zero)
                {
                    var now = System.Threading.Interlocked.Increment(ref _inFlight);
                    lock (Seen) MaxCheckCallsInFlight = Math.Max(MaxCheckCallsInFlight, now);
                    try { await Task.Delay(CheckDelay, ct).ConfigureAwait(false); } finally { System.Threading.Interlocked.Decrement(ref _inFlight); }
                }
                if (isCheck && CheckRecordings.IsItem(Test) && request.Questions.All(q => _script(request, q) == null))
                {
                    var answered = await CheckRecordings.AnswerAsync(request, Test, ct).ConfigureAwait(false);
                    if (answered != null) { lock (Recorded) Recorded.Add(request.Digest); return answered; }
                }
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    var a = _script(request, q);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Kind == DecisionQuestionKind.Noul)
                    {
                        var p = a?.P ?? 0.97;
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append(p.ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                    else
                    {
                        var (choice, confidence) = a ?? (q.HasOption("none") ? "none" : q.Options[0].Id, 0.95);
                        sb.Append("{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                          .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture));
                        var probs = Probabilities?.Invoke(request, q);
                        if (probs != null) sb.Append(",\"probabilities\":").Append(JsonSerializer.Serialize(probs));
                        sb.Append('}');
                    }
                }
                sb.Append("}}]}");
                return await new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct).ConfigureAwait(false);
            }
        }

        /// <summary>The value_matches id Jev's filter question for a column is named by, read from the request.</summary>
        private static string MatchId(DecisionRequest r, string column)
        {
            var matches = (IEnumerable<object>)r.Snapshot["value_matches"];
            foreach (var m in matches.Cast<IDictionary<string, object>>())
                if ((string)m["column"] == column) return (string)m["id"];
            return null;
        }

        // ---- the rig ------------------------------------------------------------------------------------------

        private sealed class Rig : IDisposable
        {
            public DecisionFixture F;
            public FakeData Data = new FakeData();
            public ScriptedOracle Jev;
            public LocalEngine Engine => F.Engine;
            public void Dispose() => F.Dispose();
        }

        private static void LabelProdForTest(string endpoint, string database)
        {
            var r = ConnectionRegistry.Remember("xmla", endpoint, database, database, null, "azcli");
            ConnectionRegistry.SetLabel(r.Id, "prod", "human");
        }

        private static async Task<Rig> BuildAsync(IEntitlement tier = null, bool connect = true, bool bothWays = false,
            Func<DecisionRequest, DecisionQuestion, (string, double)?> script = null, bool? barMet = true,
            bool twins = false, bool calendar = false, (string Ref, string[] Terms)[] synonyms = null, bool orderDate = false, bool ratio = false, bool metric = false, Func<LocalEngine, FakeData, Task> shape = null,
            [System.Runtime.CompilerServices.CallerMemberName] string test = null)
        {
            var rig = new Rig { F = new DecisionFixture(tier ?? TestEntitlements.Pro) };
            var e = rig.Engine;
            var key = string.Join("|", "contoso", bothWays, twins, calendar, orderDate, ratio, metric,
                string.Join(";", (synonyms ?? Array.Empty<(string Ref, string[] Terms)>()).Select(x => x.Ref + "=" + string.Join(",", x.Terms))));
            await _templates.OpenCopyAsync(rig.F, key, g => BuildContosoModelAsync(g.Engine, bothWays, twins, calendar, synonyms, orderDate, ratio, metric), "ContosoRetail.bim");
            if (shape != null)
            {
                await shape(e, rig.Data);
                await rig.F.SaveAsAsync("ContosoRetail.bim");   // the file on disk is the shaped model, as the old build saved it
            }
            await rig.F.ConsentAsync();
            if (connect)
            {
                e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", rig.Data.Execute));
                LabelProdForTest("powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail");   // an unlabelled target counts as test since 1.4; these tests are about the production rule
            }
            e.JevReadsDataForTest = barMet;
            // The connected copy holds what was built, so an edit a test makes to the open copy afterwards is unpublished.
            rig.Data.Connected = (await CatalogAsync(rig)).Measures.ToDictionary(m => m.Name, m => (m.Expression, m.FormatString));
            rig.Jev = new ScriptedOracle(script ?? ((_, __) => null)) { Test = test };
            e.UseDecisionsForTest(new DecisionService(rig.Jev, new DecisionBudget()));
            return rig;
        }

        /// <summary>The model every rig asks about, as a list of engine calls. Split out of BuildAsync so a test class can build it once
        /// and open copies of it (ModelTemplates), and so a test can prove the copy is the same model.</summary>
        internal static async Task BuildContosoModelAsync(LocalEngine e, bool bothWays = false, bool twins = false, bool calendar = false,
            (string Ref, string[] Terms)[] synonyms = null, bool orderDate = false, bool ratio = false, bool metric = false)
        {
            await e.CreateModelAsync("Contoso Retail", 1604);
            async Task Table(string name, params (string col, string type)[] cols)
            {
                await e.CreateTableAsync(name, "human");
                foreach (var (col, type) in cols) await e.CreateColumnAsync("table:" + name, col, type, col, "human");
            }
            await Table("Sales", ("Amount", "Double"), ("Qty", "Int64"), ("StoreKey", "Int64"), ("ProductKey", "Int64"), ("DateKey", "Int64"), ("CustomerKey", "Int64"));
            await Table("Store", ("StoreKey", "Int64"), ("Region", "String"), ("City", "String"));
            await Table("Product", ("ProductKey", "Int64"), ("Category", "String"), ("ProductName", "String"));
            await Table("Date", ("DateKey", "Int64"), ("Year", "Int64"), ("MonthName", "String"));
            await Table("Customer", twins ? new[] { ("CustomerKey", "Int64"), ("Segment", "String"), ("Region", "String") } : new[] { ("CustomerKey", "Int64"), ("Segment", "String") });
            // A real calendar: a column of days and one naming each month of each year.
            if (calendar) { await e.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "human"); await e.CreateColumnAsync("table:Date", "YearMonth", "String", "YearMonth", "human"); }
            // No calendar, but the sales rows carry the day they were made on.
            if (orderDate) await e.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", "OrderDate", "human");
            await Table("Budget", ("DateKey", "Int64"), ("BudgetAmount", "Double"));
            // K16: a column whose values name numbers ("Sales", "Margin"), on a table the sales rows reach, so narrowing by it is
            // one real ask away.
            if (metric)
            {
                await Table("Metric Category", ("MetricKey", "Int64"), ("Metric Category", "String"));
                await e.CreateColumnAsync("table:Sales", "MetricKey", "Int64", "MetricKey", "human");
                await e.CreateRelationshipAsync("column:Sales/MetricKey", "column:Metric Category/MetricKey", null, true, "human");
                // Kane's model's shape: a brand on the product, and a second sales number.
                await e.CreateColumnAsync("table:Product", "Brand", "String", "Brand", "human");
            }
            await e.CreateRelationshipAsync("column:Sales/StoreKey", "column:Store/StoreKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", bothWays ? "BothDirections" : null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/DateKey", "column:Date/DateKey", null, true, "human");
            // Inactive, so Customer reaches nothing, unless the twins rig needs a second Region that does.
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, twins, "human");
            await e.CreateRelationshipAsync("column:Budget/DateKey", "column:Date/DateKey", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Units", "SUM ( Sales[Qty] )", "human");
            await e.CreateMeasureAsync("table:Budget", "Budget Amount", "SUM ( Budget[BudgetAmount] )", "human");
            await e.CreateMeasureAsync("table:Product", "Product Count", "COUNTROWS ( Product )", "human");
            if (metric) await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            // A measure that is itself a ratio, for the round-5 gate (a share, a percent or an average is not asked of the engine).
            if (ratio) { await e.CreateMeasureAsync("table:Sales", "Average Price", "DIVIDE ( [Net Sales], [Units] )", "human"); await e.SetDescriptionAsync("measure:Sales/Average Price", "The average price per unit sold.", "human"); }
            foreach (var (objRef, terms) in synonyms ?? Array.Empty<(string, string[])>()) await e.SetSynonymsAsync(objRef, terms, null, "human");
        }

        private static Task<QueryCatalog> CatalogAsync(Rig rig) => rig.F.Sessions.Require().ReadAsync(m => QueryCatalog.Read(m));

        private static QuerySpec Spec(QueryCatalog cat, string[] groups, string[] measures, params (string col, string op, object[] vals)[] filters)
        {
            var s = new QuerySpec();
            s.GroupBy.AddRange(groups.Select(g => cat.Column(g) ?? throw new InvalidOperationException(g)));
            s.Measures.AddRange(measures.Select(m => cat.Measure(m) ?? throw new InvalidOperationException(m)));
            foreach (var (col, op, vals) in filters)
                s.Filters.Add(new QueryFilter { Column = cat.Column(col), Op = op, Values = vals.ToList() });
            return s;
        }

        // ---- 1. the template builds valid DAX for every shape ---------------------------------------------------

        [Fact]
        public async Task The_canonical_shape_is_exactly_the_template()
        {
            using var rig = await BuildAsync();
            var cat = await CatalogAsync(rig);
            var s = Spec(cat, new[] { "Store[Region]" }, new[] { "Net Sales" }, ("Date[Year]", QueryOps.Is, new object[] { 2025L }));
            s.Top = 10;
            Assert.Equal(
                "DEFINE\n" +
                "    VAR __Filter1 =\n" +
                "        TREATAS ( { 2025 }, 'Date'[Year] )\n" +
                "    VAR __Result =\n" +
                "        SUMMARIZECOLUMNS (\n" +
                "            'Store'[Region],\n" +
                "            __Filter1,\n" +
                "            \"@Net Sales\", [Net Sales]\n" +
                "        )\n" +
                "EVALUATE\n" +
                "    TOPN ( 10, __Result, [@Net Sales], DESC )\n" +
                "ORDER BY\n" +
                "    [@Net Sales] DESC\n",
                QueryTemplate.Build(s));
            Assert.Equal("Net Sales by Region, Year = 2025, top 10 by Net Sales", QueryTemplate.Reading(s, cat));
        }

        [Fact]
        public async Task Every_shape_builds_DAX_that_checks_out_against_the_model()
        {
            using var rig = await BuildAsync();
            var cat = await CatalogAsync(rig);
            var shapes = new List<(string name, QuerySpec spec)>
            {
                ("one total", Spec(cat, new string[0], new[] { "Net Sales" })),
                ("one split", Spec(cat, new[] { "Store[Region]" }, new[] { "Net Sales" })),
                ("two splits, two measures", Spec(cat, new[] { "Store[Region]", "Product[Category]" }, new[] { "Net Sales", "Units" })),
                ("is, text", Spec(cat, new[] { "Product[Category]" }, new[] { "Net Sales" }, ("Store[Region]", QueryOps.Is, new object[] { "Coast" }))),
                ("is, two values", Spec(cat, new[] { "Product[Category]" }, new[] { "Net Sales" }, ("Store[Region]", QueryOps.Is, new object[] { "Coast", "North" }))),
                ("is not", Spec(cat, new[] { "Product[Category]" }, new[] { "Net Sales" }, ("Store[Region]", QueryOps.IsNot, new object[] { "Coast" }))),
                ("after", Spec(cat, new[] { "Store[Region]" }, new[] { "Units" }, ("Date[Year]", QueryOps.After, new object[] { 2023L }))),
                ("from", Spec(cat, new[] { "Store[Region]" }, new[] { "Units" }, ("Date[Year]", QueryOps.From, new object[] { 2024L }))),
                ("before", Spec(cat, new[] { "Store[Region]" }, new[] { "Units" }, ("Date[Year]", QueryOps.Before, new object[] { 2025L }))),
                ("up to", Spec(cat, new[] { "Store[Region]" }, new[] { "Units" }, ("Date[Year]", QueryOps.UpTo, new object[] { 2025L }))),
                ("two filters", Spec(cat, new[] { "Product[ProductName]" }, new[] { "Net Sales" }, ("Store[Region]", QueryOps.Is, new object[] { "Coast" }), ("Date[Year]", QueryOps.Is, new object[] { 2025L }))),
            };
            var top = Spec(cat, new[] { "Product[ProductName]" }, new[] { "Net Sales", "Units" }, ("Date[Year]", QueryOps.Is, new object[] { 2025L }));
            top.Top = 10; top.TopBy = cat.Measure("Units");
            shapes.Add(("top N by the second measure", top));
            var bottom = Spec(cat, new[] { "Product[ProductName]" }, new[] { "Net Sales" });
            bottom.Top = 5; bottom.Lowest = true;
            shapes.Add(("bottom N", bottom));
            var byMeasure = Spec(cat, new[] { "Store[Region]" }, new[] { "Net Sales" });
            byMeasure.SortMeasure = cat.Measure("Net Sales"); byMeasure.Descending = true;
            shapes.Add(("sorted by a measure", byMeasure));
            var byColumn = Spec(cat, new[] { "Store[Region]", "Date[Year]" }, new[] { "Net Sales" });
            byColumn.SortColumn = cat.Column("Date[Year]"); byColumn.Descending = true;
            shapes.Add(("sorted by a column", byColumn));

            foreach (var (name, spec) in shapes)
            {
                var dax = QueryTemplate.Build(spec);
                var v = await rig.F.Sessions.Require().ReadAsync(m => DaxValidator.Validate(m, dax));
                var notes = v.Diagnostics.Where(d => (d.Message ?? "").IndexOf("@", StringComparison.Ordinal) < 0).Select(d => d.Severity + ": " + d.Message).ToList();
                Assert.True(v.Valid, name + ": " + string.Join("; ", notes));
                Assert.True(notes.Count == 0, name + " has notes: " + string.Join("; ", notes) + "\n" + dax);
                Assert.StartsWith("DEFINE\n", dax);
                Assert.Contains("SUMMARIZECOLUMNS (", dax);
                Assert.Equal(dax.Count(c => c == '('), dax.Count(c => c == ')'));
            }
        }

        [Fact]
        public void Names_and_values_with_quotes_and_brackets_are_escaped()
        {
            var cat = new QueryCatalog();
            var col = new QueryColumn { Table = "Bob's Stores", Name = "Zone]X", Kind = "text" };
            var m = new QueryMeasure { Table = "Bob's Stores", Name = "Sales [net]", Reads = new[] { "Bob's Stores" } };
            cat.Columns.Add(col);
            cat.Measures.Add(m);
            var s = new QuerySpec();
            s.GroupBy.Add(col);
            s.Measures.Add(m);
            s.Filters.Add(new QueryFilter { Column = col, Op = QueryOps.Is, Values = { "He said \"hi\"" } });
            var dax = QueryTemplate.Build(s);
            Assert.Contains("'Bob''s Stores'[Zone]]X]", dax);
            Assert.Contains("\"He said \"\"hi\"\"\"", dax);
            Assert.Contains("\"@Sales [net]\", [Sales [net]]]", dax);
            Assert.Contains("ORDER BY\n    'Bob''s Stores'[Zone]]X] ASC", dax);
        }

        // ---- 2. the reading line matches the query ------------------------------------------------------------

        [Fact]
        public async Task The_reading_line_names_exactly_what_the_query_does()
        {
            using var rig = await BuildAsync();
            var cat = await CatalogAsync(rig);
            var s1 = Spec(cat, new[] { "Store[Region]", "Product[Category]" }, new[] { "Net Sales", "Units" },
                ("Store[Region]", QueryOps.IsNot, new object[] { "Coast", "North" }), ("Date[Year]", QueryOps.From, new object[] { 2024L }));
            Assert.Equal("Net Sales and Units by Region and Category, Region is not Coast or North, Year from 2024", QueryTemplate.Reading(s1, cat));
            var s2 = Spec(cat, new[] { "Product[ProductName]" }, new[] { "Units" });
            s2.Top = 5; s2.Lowest = true;
            Assert.Equal("Units by ProductName, bottom 5 by Units", QueryTemplate.Reading(s2, cat));
            Assert.Contains("TOPN ( 5, __Result, [@Units], ASC )", QueryTemplate.Build(s2));
            var s3 = Spec(cat, new[] { "Store[Region]" }, new[] { "Net Sales" });
            s3.SortMeasure = cat.Measure("Net Sales"); s3.Descending = true;
            Assert.Equal("Net Sales by Region, highest Net Sales first", QueryTemplate.Reading(s3, cat));
            Assert.Contains("ORDER BY\n    [@Net Sales] DESC", QueryTemplate.Build(s3));
            Assert.Equal("Net Sales", QueryTemplate.Reading(Spec(cat, new string[0], new[] { "Net Sales" }), cat));

            // Each part named in the reading is a part of the query, and the other way round.
            foreach (var s in new[] { s1, s2, s3 })
            {
                var dax = QueryTemplate.Build(s);
                var reading = QueryTemplate.Reading(s, cat);
                foreach (var m in s.Measures) { Assert.Contains(m.Name, reading); Assert.Contains("[" + m.Name + "]", dax); }
                foreach (var c in s.GroupBy) { Assert.Contains(cat.Shown(c), reading); Assert.Contains(c.Dax, dax); }
                foreach (var f in s.Filters) foreach (var v in f.Values) { Assert.Contains(Convert.ToString(v, CultureInfo.InvariantCulture), reading); Assert.Contains(QueryTemplate.Literal(f.Column, v), dax); }
            }
        }

        // ---- 3. reachability ----------------------------------------------------------------------------------

        [Fact]
        public async Task A_column_that_cannot_reach_the_measure_is_refused_and_nothing_runs()
        {
            using var rig = await BuildAsync();
            var budget = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Budget Amount" }, GroupBy = new[] { "'Store'[Region]" } });
            Assert.Equal(DataAnswer.Refused, budget.Outcome);
            var reach = Assert.Single(budget.Checks, c => c.Name == "reach");
            Assert.False(reach.Passed);
            Assert.Equal("Region cannot narrow Budget Amount: no active relationship leads from Budget to Store.", reach.Line);

            // An inactive relationship does not count: Customer reaches Sales only through one.
            var inactive = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "Customer[Segment]" } });
            Assert.Equal(DataAnswer.Refused, inactive.Outcome);
            Assert.Contains(inactive.Checks, c => c.Name == "reach" && !c.Passed);

            // A filter is held to the same rule as a split.
            var filtered = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Budget Amount" }, GroupBy = new[] { "Date[Year]" },
                Filters = new[] { new DataAskFilter { Column = "Store[Region]", Op = "is", Values = new[] { "Coast" } } },
            });
            Assert.Equal(DataAnswer.Refused, filtered.Outcome);
            Assert.Contains(filtered.Checks, c => c.Name == "reach" && !c.Passed);

            Assert.Empty(rig.Data.Runs);

            // The same measure by a table that does reach it runs.
            var ok = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Budget Amount" }, GroupBy = new[] { "Date[Year]" } });
            Assert.Equal(DataAnswer.Answered, ok.Outcome);
            Assert.Contains(ok.Checks, c => c.Name == "reach" && c.Passed);
            Assert.Single(rig.Data.Runs);
        }

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_relationship_that_filters_both_ways_lets_the_column_reach()
        {
            using (var one = await BuildAsync())
            {
                var a = await one.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Product Count" }, GroupBy = new[] { "Store[Region]" } });
                Assert.Equal(DataAnswer.Refused, a.Outcome);
            }
            using var both = await BuildAsync(bothWays: true);
            var b = await both.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Product Count" }, GroupBy = new[] { "Store[Region]" } });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
        }

        // ---- filter values, matched on this computer ----------------------------------------------------------

        [Fact]
        public async Task Filter_words_become_the_models_own_values_or_are_refused()
        {
            using var rig = await BuildAsync();
            var a = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" }, GroupBy = new[] { "Product[Category]" },
                Filters = new[] { new DataAskFilter { Column = "Store[Region]", Values = new[] { "coast" } }, new DataAskFilter { Column = "Date[Year]", Values = new[] { "2025" } } },
            });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal("Net Sales by Category, Region = Coast, Year = 2025", a.Reading);
            Assert.Contains("TREATAS ( { \"Coast\" }, 'Store'[Region] )", a.Dax);
            Assert.Contains("TREATAS ( { 2025 }, 'Date'[Year] )", a.Dax);
            Assert.Equal("you", a.ReadBy);

            var typo = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" }, Filters = new[] { new DataAskFilter { Column = "Product[ProductName]", Values = new[] { "Trail Tnet" } } },
            });
            Assert.Equal("Net Sales, ProductName = Trail Tent", typo.Reading);

            var runsBefore = rig.Data.Runs.Count();
            var none = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" }, Filters = new[] { new DataAskFilter { Column = "Store[Region]", Values = new[] { "Atlantis" } } },
            });
            Assert.Equal(DataAnswer.Refused, none.Outcome);
            Assert.Equal("No value in Region matches 'Atlantis'.", Assert.Single(none.Checks, c => c.Name == "values").Line);
            var year = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" }, Filters = new[] { new DataAskFilter { Column = "Date[Year]", Values = new[] { "2019" } } },
            });
            Assert.Equal(DataAnswer.Refused, year.Outcome);
            Assert.Equal(runsBefore, rig.Data.Runs.Count());

            // A comparison needs a number, not a value the column holds.
            var since = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" }, Filters = new[] { new DataAskFilter { Column = "Date[Year]", Op = "after", Values = new[] { "2019" } } },
            });
            Assert.Equal(DataAnswer.Answered, since.Outcome);
            Assert.Equal("Net Sales, Year after 2019", since.Reading);
        }

        [Fact]
        public async Task The_answer_is_capped_and_its_columns_carry_the_plain_names()
        {
            using var rig = await BuildAsync();
            rig.Data.RowsToReturn = 600;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales", "Units" }, GroupBy = new[] { "Store[Region]" }, MaxRows = 500 });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.True(a.Truncated);
            Assert.Equal(500, a.Rows.Length);
            Assert.Equal(new[] { "Region", "Net Sales", "Units" }, a.Columns.Select(c => c.Name).ToArray());
            Assert.StartsWith("Stopped at 500 rows.", Assert.Single(a.Checks, c => c.Name == "rows").Line);

            var built = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, Run = false });
            Assert.Equal(DataAnswer.Built, built.Outcome);
            Assert.NotNull(built.Dax);
            Assert.Single(rig.Data.Runs);
        }

        // ---- Jev reads the words -------------------------------------------------------------------------------

        private static (string, double)? CoastScript(DecisionRequest r, DecisionQuestion q)
        {
            if (q.Name == JevQueryReader.MeasureQ(1)) return ("m:Net Sales", 0.97);
            if (q.Name == JevQueryReader.SplitsQ) return ("1", 0.95);
            if (q.Name == JevQueryReader.GroupQ(1)) return ("c:Product[Category]", 0.93);
            if (q.Name == JevQueryReader.FilterQ(MatchId(r, "Store[Region]") ?? "-")) return (QueryOps.Is, 0.96);
            if (q.Name == JevQueryReader.FilterQ(MatchId(r, "Date[Year]") ?? "-")) return (QueryOps.Is, 0.95);
            if (q.Name == JevQueryReader.TopQ) return ("n:3", 0.91);
            if (q.Name == JevQueryReader.TopByQ) return ("m:Net Sales", 0.95);
            if (q.Name == JevQueryReader.TopEndQ) return ("highest", 0.97);
            return null;
        }

        private const string CoastQuestion = "net sales by category in the coast region for 2025, top 3";
        private const string CityQuestion = "net sales by city in the coast region for 2025, top 3";

        [Fact]
        public async Task Jev_reads_a_question_and_the_engine_builds_checks_and_runs_it()
        {
            using var rig = await BuildAsync(script: CoastScript);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal("jev", a.ReadBy);
            Assert.Equal("Net Sales by Category, Region = Coast, Year = 2025, top 3 by Net Sales", a.Reading);
            Assert.Contains("TOPN ( 3, __Result, [@Net Sales], DESC )", a.Dax);
            Assert.Contains(a.Checks, c => c.Name == "reach" && c.Passed);
            Assert.Contains(a.Checks, c => c.Name == "dax" && c.Passed);
            Assert.Equal(3, a.Rows.Length);
            // Two calls: the parts, then the split from the engine's shortlist.
            Assert.Equal(2, rig.Jev.Seen.Count);
            foreach (var request in rig.Jev.Seen)
            {
                Assert.Equal(DecisionRegistry.QuerySummarize, request.PointName);
                Assert.All(request.Questions, q => Assert.StartsWith(JevQueryReader.Prefix, q.Name));
                Assert.True(request.Questions.Count <= JevQueryReader.MaxQuestions);
            }

            // The parts come back in the ask's own shape: change one and ask again without Jev.
            var edited = a.Parts;
            edited.Top = 2;
            edited.Question = CoastQuestion;
            var again = await rig.Engine.AskDataAsync(edited);
            Assert.Equal("you", again.ReadBy);
            Assert.Equal("Net Sales by Category, Region = Coast, Year = 2025, top 2 by Net Sales", again.Reading);
            Assert.Equal(2, rig.Jev.Seen.Count);
        }

        /// <summary>K19 (Kane, 8 Oct): with no permission the note had no way to give it. The answer now says the person can allow it;
        /// the page's one Allow press gives it (set_consent as the person), and the same question then gets its number.</summary>
        [Fact]
        public async Task K19_a_question_with_no_permission_says_it_can_be_allowed_and_gets_its_number_once_it_is()
        {
            using var rig = await BuildAsync(script: CoastScript);
            await rig.Engine.WithdrawConsentAsync("human");

            var stopped = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.NeedsYou, stopped.Outcome);
            Assert.True(stopped.NeedsPermission);
            Assert.Empty(rig.Jev.Seen);

            var info = await rig.Engine.GetConsentAsync();
            await rig.Engine.SetConsentAsync(info.FieldsInForce, info.SchemaVersionInForce, "human", info.AppliesTo);

            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.False(a.NeedsPermission);
            Assert.Equal(3, a.Rows.Length);
        }

        [Fact]
        public async Task A_part_Jev_is_not_sure_of_runs_the_best_guess_and_is_said_plainly()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.GroupQ(1) ? ("c:Product[Category]", 0.62) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("Split by Category.", a.Assumption ?? "");
            Assert.Single(rig.Data.Runs);

            // A matched value Jev leans away from, below the bar, is left out and said (K16).
            (string, double)? Script2(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.FilterQ(MatchId(r, "Store[Region]") ?? "-") ? ("none", 0.55) : CoastScript(r, q);
            using var rig2 = await BuildAsync(script: Script2);
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.True(Answered(b), Seen(b));
            Assert.Contains("Region is not filtered.", b.Assumption ?? "");
            Assert.Single(rig2.Data.Runs);

            // A question Jev reads, sure, as not fitting the model says so.
            using var rig3 = await BuildAsync(script: (r, q) => q.Name == JevQueryReader.Fits ? ("no", 0.03) : null);
            var c = await rig3.Engine.AskDataAsync(new DataAsk { Question = "what is the weather in the coast region" });
            Assert.Equal(DataAnswer.NeedsYou, c.Outcome);
            Assert.StartsWith("This model may not hold that.", Assert.Single(c.Unsure));
            Assert.Empty(rig3.Data.Runs);
        }

        [Fact]
        public async Task Values_are_matched_here_and_only_the_questions_own_words_reach_Jev()
        {
            using var rig = await BuildAsync(script: CoastScript);
            const string question = "net sales by category in the coast region for 2025 and the Harbourview city";
            await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            Assert.Equal(2, rig.Jev.Seen.Count);
            var request = rig.Jev.Seen[0];

            // Everything that leaves, over every call (the check too): the snapshot and every question's words and options.
            var wire = new StringBuilder();
            Assert.Single(FirstChecks(rig));
            foreach (var sentRequest in rig.Jev.All)
            {
                wire.Append(DecisionDigest.CanonicalSnapshotJson(sentRequest.Snapshot));
                foreach (var q in sentRequest.Questions)
                {
                    wire.Append('\n').Append(q.Name).Append('\n').Append(q.Instructions);
                    foreach (var o in q.Options) wire.Append('\n').Append(o.Id).Append('\n').Append(o.Criterion);
                }
            }
            var sent = wire.ToString();

            // No value of the data goes unless the person typed it; the typo-matched "Harborview" never goes.
            foreach (var v in Values.Values.SelectMany(x => x).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)))
                if (question.IndexOf(v, StringComparison.OrdinalIgnoreCase) < 0)
                    Assert.True(sent.IndexOf(v, StringComparison.OrdinalIgnoreCase) < 0, "a value the question does not contain was sent: " + v);
            foreach (var s in Sentinels) Assert.DoesNotContain(s, sent, StringComparison.OrdinalIgnoreCase);

            // What a match sends is the column and the question's own words.
            var matches = ((IEnumerable<object>)request.Snapshot["value_matches"]).Cast<IDictionary<string, object>>().ToList();
            Assert.Contains(matches, m => (string)m["column"] == "Store[Region]" && (string)m["words"] == "coast");
            Assert.Contains(matches, m => (string)m["column"] == "Store[City]" && (string)m["words"] == "Harbourview");
            Assert.All(matches, m => Assert.Contains((string)m["words"], question));
            Assert.Equal(new[] { "question_words", "model_fields", "value_matches" }.OrderBy(x => x), request.Snapshot.Keys.OrderBy(x => x));
        }


        // ---- the split: an engine shortlist, then simple choices ---------------------------------------------

        [Fact]
        public async Task The_split_is_chosen_from_a_short_list_of_columns_that_reach_the_measure()
        {
            using var rig = await BuildAsync(script: CoastScript);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, a.Outcome);

            // The first call settles the parts and how many splits; it asks about no column to split by.
            var first = rig.Jev.Seen[0];
            Assert.DoesNotContain(first.Questions, q => q.Name.StartsWith(JevQueryReader.Prefix + "group", StringComparison.Ordinal));
            Assert.Contains(first.Questions, q => q.Name == JevQueryReader.SplitsQ);

            // The second offers a few columns, every one reaching Net Sales, each saying which words matched it, and a way out.
            var second = rig.Jev.Seen[1];
            var group = Assert.Single(second.Questions, q => q.Name == JevQueryReader.GroupQ(1));
            var columns = group.Options.Where(o => o.Id.StartsWith("c:", StringComparison.Ordinal)).ToList();
            Assert.InRange(columns.Count, 1, JevQueryReader.MaxShortlist);
            Assert.Equal("c:Product[Category]", columns[0].Id);
            Assert.Contains("the words 'category' match its name", columns[0].Criterion);
            Assert.True(group.HasOption(JevQueryReader.Other));
            Assert.DoesNotContain(columns, c => c.Id.StartsWith("c:Budget[", StringComparison.Ordinal));     // Budget does not reach Sales
            Assert.DoesNotContain(columns, c => c.Id.StartsWith("c:Customer[", StringComparison.Ordinal));   // an inactive relationship
            Assert.DoesNotContain(columns, c => c.Id.EndsWith("Key]", StringComparison.Ordinal));            // keys are never splits

            var tables = Assert.Single(second.Questions, q => q.Name == JevQueryReader.GroupTableQ(1));
            Assert.Contains(tables.Options, o => o.Id == "t:Store");
            // The way out lists every table, so a split that cannot reach is refused plainly by the reach check.
            Assert.Contains(tables.Options, o => o.Id == "t:Budget");
        }

        [Fact]
        public void The_shortlist_ranks_by_name_synonym_and_folder_and_keeps_to_reachable_columns()
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "Product", Name = "ProductName", Kind = "text" });
            cat.Columns.Add(new QueryColumn { Table = "Product", Name = "Category", Kind = "text" });
            cat.Columns.Add(new QueryColumn { Table = "Product", Name = "ProductKey", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "Store", Name = "Region", Kind = "text", Synonyms = new[] { "territory" } });
            cat.Columns.Add(new QueryColumn { Table = "Store", Name = "Channel", Kind = "text", Folder = "Selling route" });
            cat.Columns.Add(new QueryColumn { Table = "Budget", Name = "Scenario", Kind = "text" });
            cat.Edges.Add(("Sales", "Product", false));
            cat.Edges.Add(("Sales", "Store", false));
            var net = new QueryMeasure { Table = "Sales", Name = "Net Sales", Reads = new[] { "Sales" } };
            cat.Measures.Add(net);

            string Top(string words) => QuerySplitShortlist.For(words, cat, new[] { net }).First().Id;
            Assert.Equal("Product[ProductName]", Top("top 5 products by net sales"));
            Assert.Equal("Store[Region]", Top("net sales per territory"));
            Assert.Equal("Store[Channel]", Top("net sales by selling route"));
            Assert.Equal("Product[Category]", Top("net sales by categroy"));   // a slip of the fingers
            // Twins answer to the same words: only the one a person reads is offered, never its sort helper.
            cat.Columns.Add(new QueryColumn { Table = "Date", Name = "MonthName", Kind = "text" });
            cat.Columns.Add(new QueryColumn { Table = "Date", Name = "MonthNumber", Kind = "whole", SortHelper = true });
            cat.Columns.Add(new QueryColumn { Table = "Product", Name = "SKU", Kind = "text" });
            cat.Edges.Add(("Sales", "Date", false));
            var month = QuerySplitShortlist.For("net sales by month", cat, new[] { net });
            Assert.Equal("Date[MonthName]", month.First().Id);
            Assert.DoesNotContain(month, c => c.Name == "MonthNumber");
            // A column named only through its table gives way to one the words name: "products" is ProductName, not SKU.
            Assert.DoesNotContain(QuerySplitShortlist.For("top 5 products by net sales", cat, new[] { net }), c => c.Name == "SKU");
            Assert.Equal("Product[SKU]", Top("net sales per sku"));
            // A twin that the words name exactly wins over one they only half name: "by year" is Year, not YearMonth
            // (found by the fresh held-out run, where YearMonth was offered and Year was folded away).
            cat.Columns.Add(new QueryColumn { Table = "Date", Name = "YearMonth", Kind = "text" });
            cat.Columns.Add(new QueryColumn { Table = "Date", Name = "Year", Kind = "whole" });
            var year = QuerySplitShortlist.For("average order value by year", cat, new[] { net });
            Assert.Equal("Date[Year]", year.First().Id);
            Assert.DoesNotContain(QuerySplitShortlist.For("net sales by scenario", cat, new[] { net }), c => c.Table == "Budget");
            Assert.DoesNotContain(QuerySplitShortlist.For("net sales by product key", cat, new[] { net }), c => c.Key);
        }

        /// <summary>verify-askdata-r2: the stricter reach rule (a split must narrow EVERY table a measure counts, not just
        /// one) only differs for a measure with two fact tables, which neither replay model has, so nothing tested it. A
        /// column that reaches only Sales would split the Sales half and repeat the Budget total on every row.</summary>
        [Fact]
        public void The_shortlist_offers_only_splits_that_narrow_every_table_a_measure_counts()
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "Store", Name = "Region", Kind = "text" });
            cat.Columns.Add(new QueryColumn { Table = "Date", Name = "Year", Kind = "whole" });
            cat.Columns.Add(new QueryColumn { Table = "Scenario", Name = "Scenario Region", Kind = "text" });
            cat.Edges.Add(("Sales", "Store", false));
            cat.Edges.Add(("Sales", "Date", false));
            cat.Edges.Add(("Budget", "Date", false));
            cat.Edges.Add(("Budget", "Scenario", false));
            var net = new QueryMeasure { Table = "Sales", Name = "Net Sales", Reads = new[] { "Sales" } };
            var gap = new QueryMeasure { Table = "Sales", Name = "Sales vs Budget", Reads = new[] { "Sales", "Budget" } };
            cat.Measures.Add(net);
            cat.Measures.Add(gap);
            Assert.Equal(new[] { "Sales", "Budget" }, cat.Roots(gap));

            // One fact table: Store reaches it, so Store[Region] is offered.
            Assert.Contains(QuerySplitShortlist.For("net sales by region", cat, new[] { net }), c => c.Id == "Store[Region]");
            // Two: Store reaches Sales but not Budget, and Scenario reaches Budget but not Sales, so neither region is
            // offered; Date reaches both, so the year is.
            var byRegion = QuerySplitShortlist.For("sales vs budget by region", cat, new[] { gap });
            Assert.DoesNotContain(byRegion, c => c.Table == "Store");
            Assert.DoesNotContain(byRegion, c => c.Table == "Scenario");
            Assert.Equal("Date[Year]", Assert.Single(QuerySplitShortlist.For("sales vs budget by year", cat, new[] { gap })).Id);
            // Asked together, a split must reach every table of every measure.
            Assert.DoesNotContain(QuerySplitShortlist.For("net sales and sales vs budget by region", cat, new[] { net, gap }), c => c.Table == "Store");
            // The same rule the reach check holds a question to, so what is offered is never then refused.
            Assert.All(QuerySplitShortlist.For("sales vs budget by region and year", cat, new[] { gap }), c => Assert.Empty(cat.Unreached(c.Table, gap)));
        }

        [Fact]
        public async Task A_split_the_shortlist_missed_is_settled_by_the_table_then_the_column()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q)
            {
                if (q.Name == JevQueryReader.GroupQ(1)) return (JevQueryReader.Other, 0.93);
                if (q.Name == JevQueryReader.GroupTableQ(1)) return ("t:Store", 0.94);
                if (q.Name == JevQueryReader.GroupInQ(1)) return ("c:Store[City]", 0.92);
                return CoastScript(r, q);
            }
            using var rig = await BuildAsync(script: Script);
            // The words name the city the script settles on (a split the words never name, "by category" settled as City, is
            // the check's to judge now: round 3's word check is retired).
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CityQuestion });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.StartsWith("Net Sales by City,", a.Reading);
            Assert.Equal(3, rig.Jev.Seen.Count);
            var third = Assert.Single(rig.Jev.Seen[2].Questions);
            Assert.Equal(JevQueryReader.GroupInQ(1), third.Name);
            Assert.All(third.Options.Where(o => o.Id != "none"), o => Assert.StartsWith("c:Store[", o.Id));
        }

        /// <summary>From the fair-model audit (m11/query-fair 21628d6a, items gct-042 "revenu by brnd", gct-043 "quantty by
        /// countrie", gct-056 "returned units per state"): on a well-prepared model no visible column that reaches the
        /// measure matched the words, so the split question was built with the way out as its only option, the question
        /// contract refused it, and the answer said "Jev was not asked" though the first call had been sure. An empty
        /// shortlist is a real answer from the engine: it goes straight to the way out, the table, with "none of these".</summary>
        [Fact]
        public async Task A_split_no_column_on_the_shortlist_fits_asks_the_table_and_is_never_left_unasked()
        {
            const string words = "net sales per state";
            using (var probe = await BuildAsync())
            {
                var cat = await CatalogAsync(probe);
                Assert.Empty(QuerySplitShortlist.For(words, cat, new[] { cat.Measure("Net Sales") }));   // the audit's shape
            }

            // A table Jev is sure of, then its column: answered, three calls, and the column question never asked alone.
            (string, double)? Script(DecisionRequest r, DecisionQuestion q)
            {
                if (q.Name == JevQueryReader.GroupTableQ(1)) return ("t:Store", 0.94);
                if (q.Name == JevQueryReader.GroupInQ(1)) return ("c:Store[Region]", 0.92);
                return CoastScript(r, q);
            }
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = words });
            Assert.Null(a.Note);
            // Settled, three calls. The model has no state; Region is the closest column. Round 3's word check stopped it by
            // naming 'per state'; round 4 retired that check, so the answer opens with what it shows ("for each Region")
            // and the check of the reading (scripted "yes" here) is the judge of whether that is what was asked.
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.StartsWith("Net Sales by Region", a.Reading);
            Assert.Equal("This shows Net Sales for each Region.", a.Statement);
            Assert.Equal(3, rig.Jev.Seen.Count);
            var second = rig.Jev.Seen[1];
            Assert.DoesNotContain(second.Questions, q => q.Name == JevQueryReader.GroupQ(1));
            var tables = Assert.Single(second.Questions, q => q.Name == JevQueryReader.GroupTableQ(1));
            Assert.True(tables.HasOption(JevQueryReader.None));
            Assert.Contains(tables.Options, o => o.Id == "t:Store");

            // A table the measure cannot reach (gct-056: returns split by a place): the split is left out and the line says why (K16).
            (string, double)? Budget(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.GroupTableQ(1) ? ("t:Budget", 0.94)
                : q.Name == JevQueryReader.GroupInQ(1) ? ("c:Budget[BudgetAmount]", 0.92) : CoastScript(r, q);
            using var rig2 = await BuildAsync(script: Budget);
            // Words that name the column, so the reach check is what refuses it (Budget is not on the shortlist either).
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = "net sales per budget amount" });
            Assert.Null(b.Note);
            Assert.True(Answered(b), Seen(b));
            Assert.Contains("not linked to Net Sales", b.Assumption ?? "");
            Assert.Empty(b.Parts.GroupBy ?? Array.Empty<string>());
            Assert.Single(rig2.Data.Runs);

            // A sure "none of these tables": the answer is one total and the line says it was not split (K16).
            using var rig3 = await BuildAsync(script: (r, q) => q.Name == JevQueryReader.GroupTableQ(1) ? (JevQueryReader.None, 0.95) : CoastScript(r, q));
            var c = await rig3.Engine.AskDataAsync(new DataAsk { Question = words });
            Assert.Null(c.Note);
            Assert.True(Answered(c), Seen(c));
            Assert.False(string.IsNullOrEmpty(c.Assumption), Seen(c));
            Assert.Equal(2, rig3.Jev.Seen.Count);
        }

        /// <summary>The same defect one level down: with no column a person can see to split by at all, there is no
        /// table to offer either. Nothing is asked, and the reading says so instead of throwing.</summary>
        [Fact]
        public void A_split_with_nothing_to_split_by_asks_nothing_and_says_so()
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "Sales", Name = "StoreKey", Kind = "whole", Key = true });
            cat.Columns.Add(new QueryColumn { Table = "Sales", Name = "Amount", Kind = "decimal", Hidden = true });
            var net = new QueryMeasure { Table = "Sales", Name = "Net Sales", Reads = new[] { "Sales" } };
            cat.Measures.Add(net);
            var reading = new JevQueryReader.Reading { Splits = 1 };
            reading.Spec.Measures.Add(net);

            var split = JevQueryReader.BuildSplit("net sales by store", cat, reading);
            Assert.Empty(split.Questions);
            Assert.Empty(JevQueryReader.ReadSplit(null, 0.8, split, reading, cat));
            Assert.Equal(JevQueryReader.NothingToSplitBy, Assert.Single(reading.Assumptions).Line);
            Assert.Empty(reading.Unsure);
        }

        // ---- what the reader knows and sends: synonyms kept here, descriptions sent in every call ------------------

        /// <summary>From the Graphicus verifier (27 Sep): the reader read column synonyms but dropped every measure and
        /// table synonym the model's language settings hold (measured on the Graphicus AI-ready Northstar: 14 measure
        /// and 8 table synonym sets, none reached the catalog). Written through set_synonyms, the same door a person
        /// and an assistant use.</summary>
        [Fact]
        public async Task The_catalog_reads_the_synonyms_of_columns_measures_and_tables()
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateLinguisticSchemaAsync(null, "agent");
            await rig.Engine.SetSynonymsAsync("column:Store/Region", new[] { "territory" }, null, "agent");
            await rig.Engine.SetSynonymsAsync("measure:Sales/Net Sales", new[] { "revenue", "takings" }, null, "agent");
            await rig.Engine.SetSynonymsAsync("table:Store", new[] { "shop", "outlet" }, null, "agent");
            var cat = await CatalogAsync(rig);
            Assert.Contains("territory", cat.Column("Store[Region]").Synonyms);
            Assert.Contains("revenue", cat.Measure("Net Sales").Synonyms);
            Assert.Contains("takings", cat.Measure("Net Sales").Synonyms);
            Assert.True(cat.TableSynonyms.TryGetValue("Store", out var store), "no synonyms read for the Store table");
            Assert.Contains("shop", store);
        }

        /// <summary>A table synonym points the shortlist at that table, the way the table's own name does: "by shop" is
        /// the Store table's name column.</summary>
        [Fact]
        public void The_shortlist_reads_a_table_through_its_synonyms()
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "Store", Name = "Store Name", Kind = "text" });
            cat.Columns.Add(new QueryColumn { Table = "Store", Name = "Store Region", Kind = "text" });
            cat.Columns.Add(new QueryColumn { Table = "Customer", Name = "Customer Name", Kind = "text" });
            cat.Edges.Add(("Sales", "Store", false));
            cat.Edges.Add(("Sales", "Customer", false));
            cat.TableSynonyms["Store"] = new[] { "shop", "outlet" };
            var net = new QueryMeasure { Table = "Sales", Name = "Net Sales", Reads = new[] { "Sales" } };
            cat.Measures.Add(net);

            var listed = QuerySplitShortlist.WithWhy("net sales by shop", cat, new[] { net });
            Assert.Equal("Store[Store Name]", listed.First().Column.Id);
            Assert.DoesNotContain(listed, x => x.Column.Table == "Customer");
            Assert.Contains("'shop'", listed.First().Why);
        }

        /// <summary>A model with more measures than one call lists puts the ones the words name first; a measure named
        /// only through its synonym ("revenue" for Net Sales) must be among them.</summary>
        [Fact]
        public void A_big_model_lists_a_measure_the_words_name_through_its_synonym()
        {
            var cat = new QueryCatalog();
            cat.Columns.Add(new QueryColumn { Table = "Date", Name = "Year", Kind = "whole" });
            for (var i = 0; i < JevQueryReader.MaxListed + 10; i++)
                cat.Measures.Add(new QueryMeasure { Table = "Sales", Name = "Metric " + i.ToString(CultureInfo.InvariantCulture), Reads = new[] { "Sales" } });
            cat.Measures.Add(new QueryMeasure { Table = "Sales", Name = "Net Sales", Synonyms = new[] { "revenue" }, Reads = new[] { "Sales" } });

            var r = JevQueryReader.Build("revenue by year", cat, new List<ValueCandidate>());
            Assert.Contains(r.Measures, m => m.Name == "Net Sales");
        }

        /// <summary>From the Graphicus verifier (27 Sep): the split calls sent names, tables and folders only, so a column
        /// or a measure whose description settles it ("the store's region, not the customer's") was described in the
        /// first call and not in the call that picks the split. Every call that picks a part now sends the descriptions,
        /// inside the point's promise: names, types, descriptions and folders, never a value.</summary>
        [Fact]
        public async Task Every_call_that_picks_a_part_sends_the_descriptions()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q)
            {
                if (q.Name == JevQueryReader.GroupQ(1)) return (JevQueryReader.Other, 0.93);
                if (q.Name == JevQueryReader.GroupTableQ(1)) return ("t:Store", 0.94);
                if (q.Name == JevQueryReader.GroupInQ(1)) return ("c:Store[City]", 0.92);
                return CoastScript(r, q);
            }
            using var rig = await BuildAsync(script: Script);
            await rig.Engine.SetDescriptionAsync("column:Store/City", "The city the store trades in.", "human");
            await rig.Engine.SetDescriptionAsync("column:Product/Category", "The product's top-level group.", "human");
            await rig.Engine.SetDescriptionAsync("measure:Sales/Net Sales", "Sales after discounts and returns.", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CityQuestion });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(3, rig.Jev.Seen.Count);

            static List<IDictionary<string, object>> Fields(DecisionRequest r, string kind) =>
                ((IEnumerable<object>)((IDictionary<string, object>)r.Snapshot["model_fields"])[kind]).Cast<IDictionary<string, object>>().ToList();
            static string DescriptionOf(List<IDictionary<string, object>> fields, string name) =>
                fields.Where(x => (string)x["name"] == name).Select(x => x.TryGetValue("description", out var d) ? (string)d : null).FirstOrDefault();

            foreach (var call in rig.Jev.Seen)
            {
                Assert.Equal("Sales after discounts and returns.", DescriptionOf(Fields(call, "measures"), "Net Sales"));
                Assert.Equal("The city the store trades in.", DescriptionOf(Fields(call, "columns"), "City"));
            }
            Assert.Equal("The product's top-level group.", DescriptionOf(Fields(rig.Jev.Seen[1], "columns"), "Category"));
        }

        /// <summary>A column the words matched a value in is asked about as a filter, so it is listed in that call with
        /// its description even in a model with more columns than one call lists.</summary>
        [Fact]
        public void A_matched_column_is_listed_even_in_a_model_too_big_to_list_whole()
        {
            var cat = new QueryCatalog();
            for (var i = 0; i < JevQueryReader.MaxListed + 10; i++)
                cat.Columns.Add(new QueryColumn { Table = "Wide", Name = "Attribute " + i.ToString(CultureInfo.InvariantCulture), Kind = "text" });
            var channel = new QueryColumn { Table = "Store", Name = "Channel", Kind = "text", Description = "How the store sells." };
            cat.Columns.Add(channel);
            cat.Measures.Add(new QueryMeasure { Table = "Sales", Name = "Net Sales", Reads = new[] { "Sales" } });
            var match = new ValueCandidate { Id = "f1", Column = channel, Words = "online", Values = new List<object> { "Online" } };

            var r = JevQueryReader.Build("net sales attribute online", cat, new List<ValueCandidate> { match });
            Assert.Contains(channel, r.Columns);
            var columns = ((IEnumerable<object>)((IDictionary<string, object>)r.Snapshot["model_fields"])["columns"]).Cast<IDictionary<string, object>>();
            Assert.Contains(columns, x => (string)x["name"] == "Channel" && (string)x["description"] == "How the store sells.");
        }

        // ---- verifier findings, m11/query-split round 1 (left failing on purpose) ---------------------------------

        /// <summary>V1. QueryCatalog.Read parses the whole linguistic schema again for EVERY object whose synonyms it
        /// wants, and it is read fresh on EVERY ask_data call (LocalEngine.Query.cs:88). It already paid one full parse
        /// per column; this lane added one per measure and one per table, so the same call costs about half again as
        /// much on exactly the models this lane is for, the ones prepared for AI, because those are the ones with a real
        /// language schema.
        ///
        /// Measured on the rig's small model (19 columns, 4 measures, 6 tables) with a 760 KB schema: QueryCatalog.Read
        /// took 2754 ms, of which the columns were 1966 ms and the measures and tables this lane added were 1028 ms.
        /// With an empty schema it took 0 ms. The cost is one parse per object, so on a real model, where the number of
        /// objects grows with the schema, it grows with the square of the model.
        ///
        /// One parse per culture is enough: read the schema once and look every object up in it. The bar here is
        /// relative, so it does not depend on how fast the machine is: a whole catalog read must cost less than ten
        /// single lookups (five failed a correct read at 5.9 on the Windows hosted runner; the defect costs about 29).</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task The_catalog_reads_the_language_schema_once_per_culture_not_once_for_every_object()
        {
            using var rig = await BuildAsync();
            var sb = new StringBuilder("{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"Entities\":{");
            for (var i = 0; i < 4000; i++)
            {
                if (i > 0) sb.Append(',');
                var n = i.ToString(CultureInfo.InvariantCulture);
                sb.Append("\"Filler").Append(n).Append(".Attribute").Append(n)
                  .Append("\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Filler").Append(n)
                  .Append("\",\"ConceptualProperty\":\"Attribute").Append(n)
                  .Append("\"}},\"State\":\"Generated\",\"Terms\":[{\"attribute ").Append(n).Append("\":{\"State\":\"Generated\"}}]}");
            }
            sb.Append("}}");
            var schema = sb.ToString();
            await rig.F.Sessions.Require().MutateAsync("human", "a model prepared for AI has a real language schema", m =>
            {
                var c = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                c.Content = schema;
            });

            // What one lookup costs on this machine, averaged over three.
            var one = await rig.F.Sessions.Require().ReadAsync(m =>
            {
                var cult = m.Cultures["en-US"];
                var col = m.Tables["Store"].Columns["Region"];
                TabularEditor.TOMWrapper.Linguistics.SynonymHelper.GetSynonyms(col, cult);
                var w = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < 3; i++) TabularEditor.TOMWrapper.Linguistics.SynonymHelper.GetSynonyms(col, cult);
                return w.Elapsed.TotalMilliseconds / 3;
            });

            await CatalogAsync(rig);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await CatalogAsync(rig);
            var whole = sw.Elapsed.TotalMilliseconds;

            // A parse per object costs one parse per column, measure and table (29 here), far above 10x; the Windows hosted runner measured 5.9 on the correct product.
            Assert.True(whole < one * 10,
                $"One ask_data call read the catalog in {whole:F0} ms, {(one <= 0 ? 0 : whole / one):F1} times the cost of a single " +
                $"synonym lookup ({one:F1} ms) on a {schema.Length.ToString(CultureInfo.InvariantCulture)} byte language schema. " +
                "The schema is parsed once per column, per measure and per table instead of once per culture.");
        }

        /// <summary>V2. The lane's own comment on SplitSnapshot says "Every column offered is listed". It made that true
        /// of the SECOND call, by keeping the shortlisted columns when the list is cut to MaxListed, and left the THIRD
        /// call as it was: there the columns handed in ARE the request's own columns, so the same sort is a no-op and
        /// the cut falls on columns the question is offering. Jev is then asked to pick among options whose type, folder
        /// and description are missing from the model_fields it is told to pick from. A table with more visible columns
        /// than one call lists is uncommon but real.</summary>
        [Fact]
        public void The_third_call_describes_every_column_it_offers()
        {
            var cat = new QueryCatalog();
            for (var i = 0; i < JevQueryReader.MaxListed + 20; i++)
                cat.Columns.Add(new QueryColumn { Table = "Wide", Name = "Attribute " + i.ToString(CultureInfo.InvariantCulture), Kind = "text", Description = "d" + i.ToString(CultureInfo.InvariantCulture) });
            cat.Edges.Add(("Sales", "Wide", false));
            var net = new QueryMeasure { Table = "Sales", Name = "Net Sales", Reads = new[] { "Sales" } };
            cat.Measures.Add(net);

            var inTable = JevQueryReader.BuildInTable("net sales by attribute", cat, new[] { net }, new[] { (1, "Wide") });
            var offered = Assert.Single(inTable.Questions, q => q.Name == JevQueryReader.GroupInQ(1)).Options
                .Where(o => o.Id != JevQueryReader.None).Select(o => o.Id).ToList();
            var columns = ((IEnumerable<object>)((IDictionary<string, object>)inTable.Snapshot["model_fields"])["columns"]).Cast<IDictionary<string, object>>().ToList();
            var described = new HashSet<string>(columns.Select(x => "c:" + (string)x["table"] + "[" + (string)x["name"] + "]"), StringComparer.Ordinal);
            var missing = offered.Where(id => !described.Contains(id)).ToList();
            Assert.True(missing.Count == 0,
                $"{missing.Count.ToString(CultureInfo.InvariantCulture)} of {offered.Count.ToString(CultureInfo.InvariantCulture)} offered columns " +
                $"were not described in the call that offers them, for example {(missing.Count > 0 ? missing[0] : "")}");
        }

        /// <summary>V3, round 2. The one-parse reader (QueryCatalog.LanguageSchema) says it reads the terms the donor reads,
        /// with one deliberate difference: an entity whose value is not an object is skipped. An entity whose Definition is
        /// not an object is not skipped. Reading its Binding throws, the catch drops the WHOLE culture, and every synonym in
        /// it is lost, where the donor (SynonymHelper.GetSynonyms) still reads every entity that comes before it. Found by a
        /// differential fuzz of the two readers: 121 of 32,000 readings, every one of them this shape. Power BI never
        /// writes it; a hand-edited or damaged schema can, and one such entry then hides every synonym in that language.</summary>
        [Fact]
        public async Task A_damaged_entity_hides_only_itself_not_every_synonym_in_its_language()
        {
            using var rig = await BuildAsync();
            const string schema = "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"Entities\":{"
                + "\"store\":{\"Binding\":{\"ConceptualEntity\":\"Store\"},\"Terms\":[{\"shop\":{\"State\":\"Authored\"}}]},"
                + "\"damaged\":{\"Definition\":\"not an object\",\"Terms\":[]}}}";
            await rig.F.Sessions.Require().MutateAsync("human", "a hand-edited language schema", m =>
            {
                var c = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                c.Content = schema;
            });
            // The donor reads Store's term: its entity comes before the damaged one.
            var donor = await rig.F.Sessions.Require().ReadAsync(m =>
                TabularEditor.TOMWrapper.Linguistics.SynonymHelper.GetSynonyms(m.Tables["Store"], m.Cultures["en-US"]));
            Assert.Equal("shop", donor);
            var cat = await CatalogAsync(rig);
            Assert.True(cat.TableSynonyms.TryGetValue("Store", out var terms) && terms.Contains("shop", StringComparer.OrdinalIgnoreCase),
                "The donor reads 'shop' for Store, but the catalog read no synonym at all for it: one entity whose Definition is " +
                "not an object dropped every term in the language.");
        }

        [Fact]
        public async Task An_unsure_split_or_a_sure_total_asks_no_more_and_never_guesses()
        {
            // No split, sure: one call, one total.
            (string, double)? Total(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.SplitsQ ? ("0", 0.96) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Total);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales in the coast region for 2025" });
            Assert.Single(rig.Jev.Seen);
            Assert.Empty(a.Parts.GroupBy ?? Array.Empty<string>());

            // Unsure how many splits: each split is asked with a way out, and only sure answers settle the count.
            (string, double)? Unsure(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.SplitsQ ? ("1", 0.6) : CoastScript(r, q);
            using var rig2 = await BuildAsync(script: Unsure);
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
            Assert.Equal(2, rig2.Jev.Seen.Count);
            Assert.True(rig2.Jev.Seen[1].Questions.Single(q => q.Name == JevQueryReader.GroupQ(2)).HasOption("none"));
            Assert.Equal(new[] { "'Product'[Category]" }, b.Parts.GroupBy);

            // ... and an unsure lean towards a new column there runs the best guess (K16).
            (string, double)? Unsure2(DecisionRequest r, DecisionQuestion q) => q.Name == JevQueryReader.GroupQ(2) ? ("c:Store[Region]", 0.5) : Unsure(r, q);
            using var rig4 = await BuildAsync(script: Unsure2);
            var d = await rig4.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.True(Answered(d), Seen(d));
            Assert.Single(rig4.Data.Runs);

            // A way out with no sure table: the best guess runs and the line says what it did (K16).
            (string, double)? Lost(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.GroupQ(1) ? (JevQueryReader.Other, 0.9) : q.Name == JevQueryReader.GroupTableQ(1) ? ("t:Store", 0.5) : CoastScript(r, q);
            using var rig3 = await BuildAsync(script: Lost);
            var c = await rig3.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.True(Answered(c), Seen(c));
            Assert.False(string.IsNullOrEmpty(c.Assumption), Seen(c));
            Assert.True(rig3.Jev.Seen.Count >= 2, "the split was asked about: " + rig3.Jev.Seen.Count);
        }

        /// <summary>From the verifier (m11/jev-query-verify 1a6086a9, Verify_a_second_split_that_repeats_the_first_is_not_reported_as_doubt):
        /// a second split that repeats the first, below the bar, is not doubt about the first. In the redesign a sure count
        /// of one asks no second split at all; when the count was not sure, a repeat or a "none" in the second slot means
        /// there is no second split, the rule measure slot 2 already uses.</summary>
        [Fact]
        public async Task A_second_split_that_repeats_the_first_is_not_reported_as_doubt()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.GroupQ(2) ? ("c:Product[Category]", 0.62) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.DoesNotContain("Not sure you meant to split by Category.", a.Unsure ?? Array.Empty<string>());

            // Not sure how many splits, so a second is asked with a way out, and Jev re-picks the first below the bar.
            (string, double)? Open(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.SplitsQ ? ("1", 0.6) : Script(r, q);
            using var rig2 = await BuildAsync(script: Open);
            var b = await rig2.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
            Assert.Equal(new[] { "'Product'[Category]" }, b.Parts.GroupBy);

            // A "none" below the bar in the second slot is no second split too.
            (string, double)? OpenNone(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.GroupQ(2) ? ("none", 0.55) : Open(r, q);
            using var rig3 = await BuildAsync(script: OpenNone);
            var c = await rig3.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, c.Outcome);
        }

        /// <summary>The same for the second measure: Jev re-picking the first measure in the second slot, below the bar, is
        /// no second number, not doubt about the first (replay tuning run 5: two readings unsure for that alone).</summary>
        [Fact]
        public async Task A_second_measure_that_repeats_the_first_is_not_reported_as_doubt()
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) =>
                q.Name == JevQueryReader.MeasureQ(2) ? ("m:Net Sales", 0.63) : CoastScript(r, q);
            using var rig = await BuildAsync(script: Script);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal(new[] { "Net Sales" }, a.Parts.Measures);
        }

        // ---- Pro only -----------------------------------------------------------------------------------------

        [Fact]
        public async Task A_free_session_is_refused_before_anything_is_read_matched_or_sent()
        {
            using var rig = await BuildAsync(tier: TestEntitlements.Free, script: CoastScript);
            var ex = await Assert.ThrowsAsync<EntitlementException>(() => rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion }));
            Assert.Contains("Ask your data is a Semanticus Pro feature", ex.Message);
            await Assert.ThrowsAsync<EntitlementException>(() => rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } }));
            await Assert.ThrowsAsync<EntitlementException>(() => rig.Engine.ListDataQuestionsAsync());
            await Assert.ThrowsAsync<EntitlementException>(() => McpTools.AskData(rig.Engine, question: CoastQuestion));

            Assert.Empty(rig.Jev.Seen);
            Assert.Equal(0, rig.Engine.Decisions.RequestsStarted);
            Assert.Empty(rig.Data.Queries);
        }

        [Fact]
        public async Task While_this_build_does_not_let_Jev_read_words_nobody_asks_Jev()
        {
            using var rig = await BuildAsync(script: CoastScript, barMet: false);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.NeedsYou, a.Outcome);
            Assert.Equal(LocalEngine.NotYetNote, a.Note);
            Assert.Empty(rig.Jev.Seen);
            Assert.Empty(rig.Data.Runs);
            // The parts still work: that is the builder.
            var b = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "Store[Region]" } });
            Assert.Equal(DataAnswer.Answered, b.Outcome);
            Assert.Empty(rig.Jev.Seen);
        }

        [Fact]
        public async Task On_the_real_path_Jev_reads_for_Pro_because_Kane_kept_it_on()
        {
            // barMet: null is the product's own answer, not a test override. Kane, 28 Sep: Jev stays on for data
            // questions and the replays measure it (fresh3, bar v5, p = 0.142, is a measurement, not a gate).
            using var rig = await BuildAsync(script: CoastScript, barMet: null);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, a.Outcome);
            Assert.Equal("jev", a.ReadBy);
            Assert.NotEmpty(rig.Jev.Seen);
            var point = (await rig.Engine.GetDecisionPointsAsync()).Single(p => p.Name == DecisionRegistry.QuerySummarize);
            Assert.True(point.Live);
            Assert.True(point.Enabled);
            var parts = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "Store[Region]" } });
            Assert.Equal(DataAnswer.Answered, parts.Outcome);
        }

        [Fact]
        public async Task The_query_switch_turns_Jev_off_and_on_at_once_from_either_door()
        {
            // barMet: null, the build's own answer (JevReads is true): the switch a person sets turns the real path off
            // and on. The build-level constant is its own fact, On_the_real_path_Jev_reads_for_Pro_because_Kane_kept_it_on.
            using var rig = await BuildAsync(script: CoastScript, barMet: null);
            // Its own target, so the agent-door grant below cannot leak into another test's "an unlabelled target asks a person".
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Query Switch", "Query Switch", rig.Data.Execute));
            LabelProdForTest("powerbi://api.powerbi.com/v1.0/myorg/Query Switch", "Query Switch");
            await McpTools.SetDecisionPointEnabled(rig.Engine, DecisionRegistry.QuerySummarize, false);
            var gate = await McpTools.AskData(rig.Engine, question: CoastQuestion);
            Assert.Equal(DataAnswer.Refused, gate.Outcome);   // the agent door's QueryData rule asks a person first
            Semanticus.Engine.ApprovalLedger.Approve(gate.ApprovalId, "human");
            var off = await McpTools.AskData(rig.Engine, question: CoastQuestion);
            Assert.Equal(DataAnswer.NeedsYou, off.Outcome);
            Assert.Equal(LocalEngine.SwitchedOffNote, off.Note);
            Assert.Empty(rig.Jev.Seen);
            Assert.Empty(rig.Data.Queries);
            // The parts still work with the switch off: that is the builder.
            var parts = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "Store[Region]" } });
            Assert.Equal(DataAnswer.Answered, parts.Outcome);

            await rig.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.QuerySummarize, true, "human");
            var on = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.Answered, on.Outcome);
            Assert.Equal("jev", on.ReadBy);
        }

        [Fact]
        public async Task With_no_model_holding_data_the_words_are_not_read_and_the_parts_are_only_built()
        {
            using var rig = await BuildAsync(connect: false, script: CoastScript);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });
            Assert.Equal(DataAnswer.NeedsYou, a.Outcome);
            Assert.Equal(LocalEngine.NoDataNote, a.Note);
            Assert.Empty(rig.Jev.Seen);
            var b = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "Store[Region]" } });
            Assert.Equal(DataAnswer.Built, b.Outcome);
            Assert.NotNull(b.Dax);
        }

        // ---- both doors, and the history ----------------------------------------------------------------------

        [Fact]
        public async Task Both_doors_answer_the_same_way_and_share_one_history_without_rows()
        {
            using var rig = await BuildAsync(script: CoastScript);
            var rpc = new EngineRpcTarget(rig.Engine);
            var viaRpc = await rpc.askData(new DataAsk
            {
                Measures = new[] { "Net Sales" }, GroupBy = new[] { "Product[Category]" },
                Filters = new[] { new DataAskFilter { Column = "Store[Region]", Values = new[] { "Coast" } } },
            });
            using var filters = JsonDocument.Parse("[{\"column\": \"'Store'[Region]\", \"op\": \"is\", \"values\": [\"Coast\"]}]");
            Task<DataAnswer> Mcp() => McpTools.AskData(rig.Engine, measures: new[] { "Net Sales" }, group_by: new[] { "column:Product/Category" }, filters: filters.RootElement.Clone());

            // The agent door is held to the QueryData rule before anything is read: an unlabelled target asks a person.
            var asked = await Mcp();
            Assert.Equal(DataAnswer.Refused, asked.Outcome);
            Assert.NotNull(asked.ApprovalId);
            Assert.Null(asked.Reading);
            Assert.Single(rig.Data.Runs);   // only the Studio's ask ran
            Semanticus.Engine.ApprovalLedger.Approve(asked.ApprovalId, "human");
            var viaMcp = await Mcp();
            Assert.Equal(DataAnswer.Answered, viaRpc.Outcome);
            Assert.Equal(viaRpc.Reading, viaMcp.Reading);
            Assert.Equal(viaRpc.Dax, viaMcp.Dax);

            await rig.Engine.AskDataAsync(new DataAsk { Question = CoastQuestion });

            var fromMcp = await McpTools.ListDataQuestions(rig.Engine);
            var fromRpc = await rpc.listDataQuestions();
            Assert.Equal(4, fromMcp.Questions.Length);
            Assert.Equal(fromMcp.Questions.Select(q => q.Id), fromRpc.Questions.Select(q => q.Id));
            var newest = fromMcp.Questions[0];
            Assert.Equal(CoastQuestion, newest.Question);
            Assert.Equal("jev", newest.ReadBy);
            Assert.Equal("Net Sales by Category, Region = Coast, Year = 2025, top 3 by Net Sales", newest.Reading);
            Assert.Equal(3, newest.RowCount);
            Assert.Equal("agent", fromMcp.Questions[1].Origin);
            Assert.Equal(DataAnswer.Answered, fromMcp.Questions[1].Outcome);
            Assert.Equal(DataAnswer.Refused, fromMcp.Questions[2].Outcome);
            Assert.Equal("human", fromMcp.Questions[3].Origin);

            // The file holds no rows: the fake rows' keys never appear in it.
            var file = System.IO.Directory.GetFiles(rig.F.Workspace, DataQuestionStore.FileName, System.IO.SearchOption.AllDirectories).Single();
            var text = System.IO.File.ReadAllText(file);
            Assert.DoesNotContain("\"k0\"", text);
            Assert.DoesNotContain("\"rows\"", text);
        }
        /// <summary>fix-askdata-final (verify-askdata-r6 L1). The engine half of r5-062 had no test: the page tests render
        /// mocked answers that already have the fixed shape, and reverting the engine (the passing "checks out" line kept,
        /// the server's raw words back in the rows line) failed none of the 42 ask_data tests. So a run the server refuses
        /// is driven through the engine here, on both doors. The query checked out against the model open here and then
        /// failed on the one it ran on, which is the r5-062 case: no line may say it checks out over that failure, the rows
        /// line is the one plain notice and names no button (L5: the assistant door has none), and the server's own words
        /// travel exactly once, in 'error'.</summary>
        [Fact]
        public async Task A_run_the_server_refuses_reads_one_plain_line_and_the_server_s_words_once_on_both_doors()
        {
            using var rig = await BuildAsync();
            const string server = "Query (4, 5) The value for 'Net Sales' cannot be determined. Either the column doesn't exist, "
                + "or there is no current row for this column. Technical Details: RootActivityId: 5e1c7d0a-0000-4000-8000-000000000001";
            rig.Data.FailWith = server;
            var studio = await new EngineRpcTarget(rig.Engine).askData(new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "Store[Region]" } });
            Task<DataAnswer> Mcp() => McpTools.AskData(rig.Engine, measures: new[] { "Net Sales" }, group_by: new[] { "column:Store/Region" });
            var held = await Mcp();
            Semanticus.Engine.ApprovalLedger.Approve(held.ApprovalId, "human");
            var assistant = await Mcp();

            foreach (var (door, a) in new[] { ("Studio", studio), ("the assistant door", assistant) })
            {
                Assert.True(a.Outcome == DataAnswer.Failed, $"{door}: outcome {a.Outcome}");
                Assert.NotNull(a.Dax);
                Assert.Equal(server, a.Error);
                Assert.DoesNotContain(a.Checks, c => c.Passed && string.Equals(c.Name, "dax", StringComparison.Ordinal));
                Assert.DoesNotContain(a.Checks, c => c.Passed && (c.Line ?? "").Contains("checks out", StringComparison.Ordinal));
                var rows = Assert.Single(a.Checks, c => string.Equals(c.Name, "rows", StringComparison.Ordinal));
                Assert.False(rows.Passed);
                Assert.Equal(LocalEngine.QueryDidNotRunLine, rows.Line);
                Assert.DoesNotContain("Show details", rows.Line, StringComparison.Ordinal);
                Assert.All(a.Checks, c => Assert.DoesNotContain("RootActivityId", c.Line ?? "", StringComparison.Ordinal));
                Assert.Single(Regex.Matches(JsonSerializer.Serialize(a), "RootActivityId"));
            }
        }

        // ---- the verifier's smaller findings (26 Sep), each failing first on 6382287a --------------------------------

        [Fact]
        public async Task A_measure_that_counts_two_fact_tables_is_refused_when_a_column_reaches_only_one()
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateMeasureAsync("table:Sales", "Plan Gap", "[Net Sales] - [Budget Amount]", "human");
            await rig.Engine.CreateMeasureAsync("table:Sales", "Share Of All Products", "DIVIDE ( [Net Sales], CALCULATE ( [Net Sales], ALL ( Product ) ) )", "human");
            var gap = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Plan Gap" }, GroupBy = new[] { "Store[Region]" } });
            Assert.Equal(DataAnswer.Refused, gap.Outcome);
            Assert.Equal("Region cannot narrow all of Plan Gap: it counts rows of Budget and Sales, and no active relationship leads from Budget to Store, so that part would show its total on every row.",
                Assert.Single(gap.Checks, c => c.Name == "reach").Line);
            // Both sets of rows are narrowed by the date, so the same measure by year runs.
            Assert.Equal(DataAnswer.Answered, (await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Plan Gap" }, GroupBy = new[] { "Date[Year]" } })).Outcome);
            // A measure that reads a dimension to remove its filters still counts one set of rows.
            Assert.Equal(DataAnswer.Answered, (await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Share Of All Products" }, GroupBy = new[] { "Store[Region]" } })).Outcome);
        }

        [Fact]
        public async Task A_question_longer_than_the_cap_is_refused_in_plain_words()
        {
            using var rig = await BuildAsync(script: CoastScript);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = new string('x', LocalEngine.MaxQuestionChars + 1) });
            Assert.Equal(DataAnswer.Refused, a.Outcome);
            Assert.Equal("The question is longer than 100,000 characters. Shorten it and ask again.", Assert.Single(a.Checks).Line);
            Assert.Empty(rig.Jev.Seen);
            var kept = (await rig.Engine.ListDataQuestionsAsync()).Questions[0].Question;
            Assert.True(kept.Length <= DataQuestionStore.KeepChars + 3, "the history keeps a long question cut");
        }

        [Fact]
        public async Task The_plain_lines_say_what_is_wrong_in_plain_words()
        {
            using var rig = await BuildAsync();
            var blank = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, Filters = new[] { new DataAskFilter { Column = " ", Values = new[] { "x" } } } });
            Assert.Equal("A filter names no column.", Assert.Single(blank.Checks, c => !c.Passed).Line);
            var after = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, Filters = new[] { new DataAskFilter { Column = "Date[Year]", Op = "after", Values = new[] { "soon" } } } });
            Assert.Equal("A filter with after needs one number for Year.", Assert.Single(after.Checks, c => !c.Passed).Line);

            rig.Data.RowsToReturn = 2500;
            var many = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "Store[Region]" }, MaxRows = 2000 });
            Assert.StartsWith("Stopped at 2,000 rows.", Assert.Single(many.Checks, c => c.Name == "rows").Line);
            Assert.Equal(2000, many.Parts.MaxRows);

            rig.Data.RowsToReturn = 3;
            var sorted = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, GroupBy = new[] { "Store[Region]" }, Top = 2, SortBy = "Store[Region]" });
            Assert.Equal("Net Sales by Region, top 2 by Net Sales, Region A to Z", sorted.Reading);
            Assert.Contains("ORDER BY\n    'Store'[Region] ASC", sorted.Dax);
        }

        [Fact]
        public async Task Filters_that_cannot_all_hold_are_refused_and_a_ranking_with_no_top_is_said()
        {
            using var rig = await BuildAsync();
            var both = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" },
                Filters = new[] { new DataAskFilter { Column = "Store[Region]", Values = new[] { "Coast" } }, new DataAskFilter { Column = "Store[Region]", Op = "is_not", Values = new[] { "Coast" } } },
            });
            Assert.Equal(DataAnswer.Refused, both.Outcome);
            Assert.Equal("These filters leave nothing to count: Region = Coast and Region is not Coast. Change one of them and ask again.", Assert.Single(both.Checks, c => c.Name == "filters").Line);
            var bounds = await rig.Engine.AskDataAsync(new DataAsk
            {
                Measures = new[] { "Net Sales" },
                Filters = new[] { new DataAskFilter { Column = "Date[Year]", Op = "after", Values = new[] { "2025" } }, new DataAskFilter { Column = "Date[Year]", Op = "before", Values = new[] { "2024" } } },
            });
            Assert.Equal(DataAnswer.Refused, bounds.Outcome);
            Assert.Empty(rig.Data.Runs);

            var ranked = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales", "Units" }, GroupBy = new[] { "Store[Region]" }, TopBy = "Units" });
            Assert.Equal(DataAnswer.Answered, ranked.Outcome);
            Assert.Equal("A ranking was given without a top few, so every row is shown. Say how many rows to keep to rank them.", ranked.Note);
        }

        [Fact]
        public async Task The_parts_go_back_whole_and_a_name_ask_data_does_not_take_is_refused()
        {
            using var rig = await BuildAsync();
            Semanticus.Engine.AgentPolicyStore.SetPreset("open", "human");
            var first = await McpTools.AskData(rig.Engine, measures: new[] { "Net Sales" }, group_by: new[] { "Store[Region]" }, top: 3, max_rows: 50);
            Assert.Equal(DataAnswer.Answered, first.Outcome);
            Assert.Equal(50, first.Parts.MaxRows);

            // The parts as the assistant receives them (camelCase), handed back with one part changed.
            using var parts = JsonDocument.Parse(JsonSerializer.Serialize(first.Parts, ModelContextProtocol.McpJsonUtilities.DefaultOptions));
            var again = await McpTools.AskData(rig.Engine, parts: parts.RootElement.Clone(), top: 2);
            Assert.Equal("Net Sales by Region, top 2 by Net Sales", again.Reading);

            using var bad = JsonDocument.Parse("{\"groupBy\": [\"Store[Region]\"], \"measures\": [\"Net Sales\"], \"groupby\": []}");
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.AskData(rig.Engine, parts: bad.RootElement.Clone()));
            Assert.StartsWith("parts carries 'groupby', which ask_data does not take.", ex.Message);

            // At the door, a top-level name the tool does not take is refused before the SDK can drop it.
            var refusal = McpTools.UnknownAskDataArgument(new[] { "measures", "groupBy" });
            Assert.StartsWith("ask_data does not take 'groupBy'.", refusal);
            Assert.Contains("Did you mean 'group_by'?", refusal);
            Assert.Null(McpTools.UnknownAskDataArgument(new[] { "measures", "group_by", "parts", "max_rows" }));
        }

        // ---- K16 (Kane, 8 Oct): every everyday question gets a number and at most one plain assumption line -----------

        /// <summary>Jev's script for the three everyday questions on the metric picker rig: Net Sales, sure; 'sales' in
        /// Metric Category leaned to "none" but short of the bar (what Kane's live Jev did); the year "is", sure; and the split
        /// the question names.</summary>
        private static Func<DecisionRequest, DecisionQuestion, (string, double)?> K16(string splits, string group = null, string metric = "none", double metricP = 0.55) =>
            (r, q) =>
                V3FilterOn(r, q, "Metric Category[Metric Category]", "sales") ? (metric, metricP)
                : q.Name == JevQueryReader.GroupQ(1) && group != null ? ("c:" + group, 0.93)
                : V3(r, q, splits: splits);

        /// <summary>K16. "sales in 2025", "sales by year", "sales by category" each answered nothing: 'sales' also matched a
        /// value of Metric Category, Jev leaned to "it does not narrow" short of the bar, and the reading stopped on "Not sure
        /// whether 'sales' should narrow Metric Category." Now each runs the obvious reading, says in one line what it
        /// assumed, and one press adds the filter it left out.</summary>
        [Fact]
        public async Task K16_everyday_questions_answer_with_one_assumption_line_and_one_press_to_change_it()
        {
            foreach (var (question, script, reading) in new[]
            {
                ("sales in 2025", K16("0"), "Net Sales, Year = 2025"),
                ("sales by year", K16("1", "Date[Year]"), "Net Sales by Year"),
                ("sales by category", K16("1", "Product[Category]"), "Net Sales by Category"),
            })
            {
                using var rig = await BuildAsync(script: script, metric: true);
                var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
                ScriptTaken(a);
                Assert.True(Answered(a), question + ": " + Seen(a));
                Assert.Equal(reading, a.Reading);
                Assert.Empty(a.Unsure);
                Assert.Single(rig.Data.Runs);
                Assert.Equal("Metric Category is not filtered.", a.Assumption);
                var add = Assert.Single(a.Others);
                Assert.Contains(add.Parts.Filters, f => f.Column == "'Metric Category'[Metric Category]" && f.Values.Contains("Sales"));
            }
        }

        /// <summary>K16. A word that names the chosen measure beats the same word as a column's value: even a sure "is" from
        /// Jev does not narrow Metric Category by 'sales' when 'sales' is the number asked for. It is offered, one press away.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task K16_a_measure_name_word_beats_the_same_word_as_a_value()
        {
            using var rig = await BuildAsync(script: K16("1", "Product[Category]", QueryOps.Is, 0.96), metric: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "sales by category" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.False(HasFilter(a, "'Metric Category'[Metric Category]"));
            Assert.Equal("Metric Category is not filtered.", a.Assumption);
        }

        /// <summary>K16. A number Jev is not sure of is its best guess: it runs and the line says which number was used.</summary>
        [Fact]
        public async Task K16_an_unsure_measure_runs_and_says_which_number_it_used()
        {
            using var rig = await BuildAsync(script: (r, q) => q.Name == JevQueryReader.MeasureQ(1) ? ("m:Net Sales", 0.6) : V3(r, q));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "sales" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Equal("Using Net Sales.", a.Assumption);
        }

        /// <summary>K16. A split Jev is not sure of is its best guess too: it runs, and the line says what it is split by.</summary>
        [Fact]
        public async Task K16_an_unsure_split_runs_and_says_what_it_is_split_by()
        {
            using var rig = await BuildAsync(script: (r, q) => q.Name == JevQueryReader.GroupQ(1) ? ("c:Product[Category]", 0.5) : V3(r, q, splits: "1"));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by category" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.True(SplitBy(a, "'Product'[Category]"), Seen(a));
            Assert.Equal("Split by Category.", a.Assumption);
        }

        /// <summary>K16. A filter on a column the number cannot reach (Customer is on an inactive relationship) is left out
        /// and said, instead of refusing the whole question.</summary>
        [Fact]
        public async Task K16_a_filter_the_number_cannot_reach_is_left_out_and_said()
        {
            using var rig = await BuildAsync(script: (r, q) => V3(r, q));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for retail" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.False(HasFilter(a, "'Customer'[Segment]"));
            Assert.Equal("Segment is not filtered: it is not linked to Net Sales.", a.Assumption);
        }

        /// <summary>K16. The last check's doubt no longer stops the run: the number is shown and the doubt is the one line.</summary>
        [Fact]
        public async Task K16_the_checks_doubt_is_said_beside_the_number_not_instead_of_it()
        {
            using var rig = await BuildAsync(script: (r, q) => q.Name == JevQueryReader.CheckGapQ ? ("change", 0.92) : V3(r, q));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how did net sales change" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Single(rig.Data.Runs);
            Assert.Equal("The question asks how the numbers changed, and Semanticus does not work out a change yet.", a.Assumption);
        }

        /// <summary>K16. A question the model cannot answer (Jev sure it does not fit) is the one stop left, and it names the
        /// closest numbers, each one press away.</summary>
        [Fact]
        public async Task K16_a_question_that_does_not_fit_names_the_closest_numbers()
        {
            using var rig = await BuildAsync(script: (r, q) => q.Name == JevQueryReader.Fits ? ("no", 0.02) : V3(r, q));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "how many staff do we have" });
            ScriptTaken(a);
            Assert.Equal(DataAnswer.NeedsYou, a.Outcome);
            Assert.Empty(rig.Data.Runs);
            Assert.StartsWith("This model may not hold that. The closest numbers are ", Assert.Single(a.Unsure));
            Assert.Contains(a.Others, o => o.Instead == "Net Sales" && o.Parts.Measures.SequenceEqual(new[] { "Net Sales" }));
        }

        /// <summary>K16, LIVE: the three everyday questions read by the real Jev (JEV_REPLAY_LIVE=1 and a development key in
        /// SEMANTICUS_JEV_KEY, read from this process and never printed) on the metric category rig. Each is answered with a
        /// number. SEMANTICUS_K16_DUMP names a folder the answers are written to as the page receives them, for the screenshots.</summary>
        [JevReplayFact]
        public async Task K16_Live_everyday_questions_are_answered_by_the_real_Jev()
        {
            var key = Environment.GetEnvironmentVariable(JevDecisionOracle.KeyVariable);
            Assert.False(string.IsNullOrWhiteSpace(key), JevDecisionOracle.KeyVariable + " is not set for the live run");
            var dump = Environment.GetEnvironmentVariable("SEMANTICUS_K16_DUMP");
            var seen = new List<string>();
            foreach (var question in new[] { "sales in 2025", "sales by year", "sales by brand" })
            {
                using var rig = await BuildAsync(metric: true);
                using var live = new JevDecisionOracle(key, new DecisionBudget());
                rig.Engine.UseDecisionsForTest(new DecisionService(live, new DecisionBudget()));
                var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
                if (!string.IsNullOrEmpty(dump))
                {
                    System.IO.Directory.CreateDirectory(dump);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dump, question.Replace(' ', '-') + ".json"),
                        JsonSerializer.Serialize(new { question, answer = a }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
                }
                seen.Add(question + ": " + Seen(a) + "; assumption '" + a.Assumption + "'");
            }
            Assert.True(seen.All(x => x.Contains("outcome answered")), string.Join("\n", seen));
        }

        [Fact]
        public async Task Two_models_opened_live_keep_their_own_questions_and_the_sidecar_leaves_them_out_of_commits()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            async Task Model(string name, string database)
            {
                await e.CreateModelAsync(name, 1604, discardUnsaved: true);
                await e.CreateTableAsync("Sales", "human");
                await e.CreateColumnAsync("table:Sales", "Amount", "Double", "Amount", "human");
                await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
                f.Sessions.Current.LiveOrigin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Contoso Live", database, null);
            }
            await Model("Contoso Live A", "Model A");
            await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } });
            Assert.Single((await e.ListDataQuestionsAsync()).Questions);
            await Model("Contoso Live B", "Model B");
            Assert.Empty((await e.ListDataQuestionsAsync()).Questions);
            await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } });
            Assert.Single((await e.ListDataQuestionsAsync()).Questions);

            var sidecar = System.IO.Path.Combine(f.Workspace, Semanticus.Engine.LayoutStore.DirName);
            Assert.Equal(2, System.IO.Directory.GetFiles(System.IO.Path.Combine(sidecar, DataQuestionStore.LiveFolder)).Length);
            var ignore = System.IO.File.ReadAllText(System.IO.Path.Combine(sidecar, ".gitignore")).Split('\n').Select(l => l.Trim()).ToList();
            Assert.Contains(DataQuestionStore.FileName, ignore);
            Assert.Contains(DataQuestionStore.LiveFolder + "/", ignore);
            Assert.Contains(Semanticus.Engine.LayoutStore.GitAddExcludes, x => x.EndsWith("/.semanticus/data-questions.json", StringComparison.Ordinal));
        }
    }
}
