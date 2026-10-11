// The query.summarize replay (Jev question-to-DAX, Kane 26 Sep): does Jev read a data question into the right parts?
//
// The live fact runs ONLY when JEV_REPLAY_LIVE is exactly "1"; otherwise xunit reports it SKIPPED, never passed. It
// drives the product's own path end to end: the real ValueMatcher matches the words against the model's values on
// this computer, the real JevQueryReader builds each request, the real JevDecisionOracle sends it (the development
// route), and the real engine reads the answer and runs every check. Only the final run is left out (Run = false):
// what is scored is the reading, and the checks still run.
//
// Two models. Northstar is the UAT fixture (JEV_REPLAY_FIXTURES names the fixtures folder: its northstar/ TMDL project
// and data/ CSV files), copied to a scratch folder so nothing is written beside it. Contoso is
// built here, Contoso-style, with its values in memory. A fake query connection answers the engine's DISTINCT reads
// from those values; nothing leaves the machine except what the product itself sends to Jev.
//
// The labels are tools/jev-replay/query/labels.json and the bar tools/jev-replay/query/bar.json, both committed before
// the first run. The word match (JevQueryWordArm) is scored on the same items as the floor Jev must not fall below.
// Raw results go OUTSIDE the repository (~/.cache/jev-replay-query/results.json, JEV_REPLAY_OUT overrides);
// tools/jev-replay/query/score.mjs turns them into aggregate numbers and the verdict. The key is read from
// ~/.config/typesafe/env into this process only, for one client, and is never printed, returned or written.
//
//   JEV_REPLAY_LIVE=1 JEV_REPLAY_FIXTURES=<the UAT fixtures folder> dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevQueryReplay"
//   node tools/jev-replay/query/score.mjs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>A replay fact that opens the UAT fixtures but calls nothing: it runs when JEV_REPLAY_FIXTURES names a
    /// folder holding northstar/, and is reported SKIPPED otherwise.</summary>
    public sealed class ReplayFixturesFactAttribute : FactAttribute
    {
        public ReplayFixturesFactAttribute()
        {
            var dir = Environment.GetEnvironmentVariable("JEV_REPLAY_FIXTURES");
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, "northstar")))
                Skip = "Needs the UAT fixtures: set JEV_REPLAY_FIXTURES to the folder holding northstar/ (tools/jev-replay/query/README.md).";
        }
    }

    [Collection("restore-root")]
    public sealed partial class JevQueryReplay
    {
        public JevQueryReplay(RestoreRootFixture fixture) { _ = fixture; }

        private static string Repo()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Semanticus.sln"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
        }

        /// <summary>JEV_REPLAY_SET=fresh, fresh2 or fresh3 replays that held-out set (fresh-labels.json, 80;
        /// fresh2-labels.json, 160; fresh3-labels.json, 168); natural-tune and natural-held replay the two halves of the
        /// natural set (natural/, 88 each, split before any run), whose Northstar questions were written for the bare
        /// arm (JEV_REPLAY_BARE=1); fresh-r2 is the round-2 fresh set (natural/fresh-r2-labels.json, 60 items from 40
        /// questions, bare arm too); fresh-r3 is round 3's (natural/fresh-r3-labels.json, 85 items from 60 questions, bare arm).
        /// The default is the old 62.</summary>
        private static string LabelFile() => Environment.GetEnvironmentVariable("JEV_REPLAY_LABELS") is { Length: > 0 } custom ? custom : Environment.GetEnvironmentVariable("JEV_REPLAY_SET") switch
        {
            "fresh" => "fresh-labels.json",
            "fresh2" => "fresh2-labels.json",
            "fresh3" => "fresh3-labels.json",
            "natural-tune" => Path.Combine("natural", "natural-tune-labels.json"),
            "natural-held" => Path.Combine("natural", "natural-held-labels.json"),
            // Review-fix round 2 (28 Sep): 40 fresh everyday questions, labelled by an independent writer and committed
            // before any run, run once at the end.
            "fresh-r2" => Path.Combine("natural", "fresh-r2-labels.json"),
            // Review-fix round 3 (29 Sep): 60 questions written by a separate session given only the models' names
            // (natural/fresh-r3-writer.md), labelled before any run, run once at the end.
            "fresh-r3" => Path.Combine("natural", "fresh-r3-labels.json"),
            _ => "labels.json",
        };

        /// <summary>JEV_REPLAY_BARE=1 strips the Northstar copy of every description and synonym before it is opened,
        /// so the natural set's Northstar arm is the raw model its questions were written for.</summary>
        private static bool Bare => Environment.GetEnvironmentVariable("JEV_REPLAY_BARE") == "1";

        private static string Key()
        {
            // The key in this process's own environment first (loaded from the key file by the caller, so HOME can be
            // scratch); the key file otherwise. Never printed, returned or written.
            var fromEnv = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "typesafe", "env");
            var m = Regex.Match(File.ReadAllText(file), @"^\s*(?:export\s+)?TYPESAFE_API_KEY\s*=\s*[""']?([^""'\s]+)", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidOperationException("No TYPESAFE_API_KEY in the key file.");
            return m.Groups[1].Value;
        }

        // ---- the Contoso-style model -------------------------------------------------------------------------

        internal static readonly Dictionary<string, object[]> ContosoValues = new Dictionary<string, object[]>(StringComparer.Ordinal)
        {
            ["Geography[Country]"] = new object[] { "Australia", "Canada", "Germany", "France", "United States" },
            ["Geography[State]"] = new object[] { "Victoria", "Queensland", "Ontario", "Bavaria", "Texas" },
            ["Geography[City]"] = new object[] { "Melbourne", "Brisbane", "Toronto", "Munich", "Austin" },
            ["Product[Product Name]"] = new object[] { "Road Bike", "Mountain Bike", "Helmet", "Water Bottle" },
            ["Product[Color]"] = new object[] { "Red", "Blue", "Black", "Silver" },
            ["Product[Brand]"] = new object[] { "Contoso", "Fabrikam", "Litware", "Adventure Works" },
            ["Product[Category]"] = new object[] { "Bikes", "Accessories", "Clothing" },
            ["Calendar[Calendar Year]"] = new object[] { 2023L, 2024L, 2025L },
            ["Calendar[Month Name]"] = new object[] { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" },
            ["Calendar[Fiscal Quarter]"] = new object[] { "FY Q1", "FY Q2", "FY Q3", "FY Q4" },
            ["Promotion[Promotion Name]"] = new object[] { "No Discount", "Summer Sale", "Holiday Offer" },
        };

        internal static async Task BuildContosoAsync(LocalEngine e)
        {
            await e.CreateModelAsync("Contoso Sales", 1604);
            async Task Table(string name, params (string col, string type)[] cols)
            {
                await e.CreateTableAsync(name, "human");
                foreach (var (col, type) in cols) await e.CreateColumnAsync("table:" + name, col, type, col, "human");
            }
            await Table("Sales", ("Revenue", "Double"), ("Quantity", "Int64"), ("GeoKey", "Int64"), ("ProductKey", "Int64"), ("DateKey", "Int64"), ("PromoKey", "Int64"));
            await Table("Geography", ("GeoKey", "Int64"), ("Country", "String"), ("State", "String"), ("City", "String"));
            await Table("Product", ("ProductKey", "Int64"), ("Product Name", "String"), ("Color", "String"), ("Brand", "String"), ("Category", "String"));
            await Table("Calendar", ("DateKey", "Int64"), ("Calendar Year", "Int64"), ("Month Name", "String"), ("Fiscal Quarter", "String"));
            await Table("Promotion", ("PromoKey", "Int64"), ("Promotion Name", "String"));
            await Table("Returns", ("ProductKey", "Int64"), ("Returned Qty", "Int64"));
            await e.CreateRelationshipAsync("column:Sales/GeoKey", "column:Geography/GeoKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/DateKey", "column:Calendar/DateKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/PromoKey", "column:Promotion/PromoKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Returns/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Revenue", "SUM ( Sales[Revenue] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Quantity", "SUM ( Sales[Quantity] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Revenue per Unit", "DIVIDE ( [Total Revenue], [Order Quantity] )", "human");
            await e.CreateMeasureAsync("table:Returns", "Returned Units", "SUM ( Returns[Returned Qty] )", "human");
        }

        // ---- the AI-ready twin of that model ---------------------------------------------------------------------

        /// <summary>What a model prepared for AI carries that a raw one does not: a description on every table, column
        /// and measure, a folder on every measure, and the words a person would use for each thing. The reader reads all
        /// three (descriptions go in every call; synonyms are matched on this computer and never sent), so a replay that
        /// scores only raw models never scores what the reader actually does on the models it is for.</summary>
        private static readonly (string Ref, string Text)[] ContosoAiDescriptions =
        {
            ("table:Sales", "One row for every line of every order: what was sold, to whom, where and when."),
            ("table:Geography", "The places stores sell from, from country down to city."),
            ("table:Product", "The things sold, with their brand, colour and group."),
            ("table:Calendar", "One row per day, with the year, month and fiscal quarter it belongs to."),
            ("table:Promotion", "The offers an order could be sold under, including no offer at all."),
            ("table:Returns", "Goods sent back, counted by product."),
            ("column:Sales/Revenue", "The money one order line brought in."),
            ("column:Sales/Quantity", "How many items one order line sold."),
            ("column:Geography/Country", "The country the selling store is in."),
            ("column:Geography/State", "The state or province the selling store is in."),
            ("column:Geography/City", "The city the selling store is in."),
            ("column:Product/Product Name", "What the thing sold is called."),
            ("column:Product/Color", "The colour the thing sold comes in."),
            ("column:Product/Brand", "The maker's name on the thing sold."),
            ("column:Product/Category", "The top group the thing sold belongs to."),
            ("column:Calendar/Calendar Year", "The calendar year a day falls in."),
            ("column:Calendar/Month Name", "The month a day falls in, named."),
            ("column:Calendar/Fiscal Quarter", "The quarter of the financial year a day falls in."),
            ("column:Promotion/Promotion Name", "The offer an order was sold under."),
            ("column:Returns/Returned Qty", "How many items came back."),
            ("measure:Sales/Total Revenue", "All the money orders brought in."),
            ("measure:Sales/Order Quantity", "How many items were sold."),
            ("measure:Sales/Revenue per Unit", "The money brought in for each item sold."),
            ("measure:Returns/Returned Units", "How many items came back."),
        };

        private static readonly (string Ref, string[] Terms)[] ContosoAiSynonyms =
        {
            ("table:Geography", new[] { "location", "place" }),
            ("table:Product", new[] { "item", "goods" }),
            ("table:Calendar", new[] { "date", "time" }),
            ("table:Promotion", new[] { "offer", "campaign" }),
            ("table:Returns", new[] { "refunds" }),
            ("column:Geography/Country", new[] { "nation" }),
            ("column:Geography/State", new[] { "province" }),
            ("column:Geography/City", new[] { "town" }),
            ("column:Product/Product Name", new[] { "item name", "model" }),
            ("column:Product/Color", new[] { "colour", "shade" }),
            ("column:Product/Brand", new[] { "make" }),
            ("column:Product/Category", new[] { "product type", "range" }),
            ("column:Calendar/Month Name", new[] { "month" }),
            ("column:Calendar/Fiscal Quarter", new[] { "quarter" }),
            ("column:Promotion/Promotion Name", new[] { "offer name", "deal" }),
            ("measure:Sales/Total Revenue", new[] { "revenue", "sales", "turnover" }),
            ("measure:Sales/Order Quantity", new[] { "units", "volume" }),
            ("measure:Sales/Revenue per Unit", new[] { "average price", "unit revenue" }),
            ("measure:Returns/Returned Units", new[] { "returns" }),
        };

        /// <summary>The same model, prepared for AI. Same tables, columns, measures and values, so a label written for
        /// one names things the other has too; what is added is the metadata a person adds when getting a model ready.</summary>
        internal static async Task BuildContosoAiAsync(LocalEngine e)
        {
            await BuildContosoAsync(e);
            foreach (var (objRef, text) in ContosoAiDescriptions) await e.SetDescriptionAsync(objRef, text, "human");
            foreach (var (objRef, terms) in ContosoAiSynonyms) await e.SetSynonymsAsync(objRef, terms, null, "human");
        }

        // ---- the fake data connection --------------------------------------------------------------------------

        private static Func<string, ResultSet> Answering(Func<string, string, object[]> distinct, Func<string, ResultSet> calendar = null) => q =>
        {
            var d = Regex.Match(q, @"DISTINCT \( '((?:[^']|'')+)'\[((?:[^\]]|\]\])+)\] \)");
            if (!d.Success) return calendar?.Invoke(q) ?? ResultSet.FromError("The replay's connection answers value reads only.");
            var vals = distinct(d.Groups[1].Value.Replace("''", "'"), d.Groups[2].Value.Replace("]]", "]")) ?? Array.Empty<object>();
            return new ResultSet { Columns = new[] { new ColumnDef { Name = "v" } }, Rows = vals.Select(v => new[] { v }).ToArray(), RowCount = vals.Length };
        };

        /// <summary>Distinct values per CSV column, typed the way the model types the column.</summary>
        private static Func<string, string, object[]> CsvValues(string dataDir, QueryCatalog cat)
        {
            var cache = new Dictionary<string, object[]>(StringComparer.Ordinal);
            return (table, column) =>
            {
                var key = table + "[" + column + "]";
                if (cache.TryGetValue(key, out var hit)) return hit;
                var file = Path.Combine(dataDir, table + ".csv");
                object[] result = Array.Empty<object>();
                if (File.Exists(file))
                {
                    var lines = File.ReadAllLines(file);
                    var header = SplitCsv(lines[0]);
                    var i = Array.IndexOf(header, column);
                    var kind = cat.Column(key)?.Kind;
                    if (i >= 0)
                        result = lines.Skip(1).Select(l => SplitCsv(l)).Where(r => r.Length > i).Select(r => r[i]).Distinct(StringComparer.Ordinal)
                            .Select(v => kind == "whole" && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (object)n : v).ToArray();
                }
                return cache[key] = result;
            };
        }

        /// <summary>The engine's two calendar reads (QueryPeriods), answered from the Northstar CSV files: the last day an
        /// order falls on, and the Date table's own labels for the days asked about. Anything else is refused.</summary>
        private static Func<string, ResultSet> CsvCalendar(string dataDir, QueryCatalog cat) => q =>
        {
            var dates = File.ReadAllLines(Path.Combine(dataDir, "Date.csv"));
            var header = SplitCsv(dates[0]);
            var rows = dates.Skip(1).Select(SplitCsv).ToList();
            int At(string col) => Array.IndexOf(header, col);
            DateTime Day(string[] r) => DateTime.Parse(r[At("Date")], CultureInfo.InvariantCulture);
            if (q.Contains("MAXX ( FILTER ( VALUES ( 'Date'[Date] )", StringComparison.Ordinal))
            {
                var sales = File.ReadAllLines(Path.Combine(dataDir, "Sales.csv"));
                var key = Array.IndexOf(SplitCsv(sales[0]), "OrderDateKey");
                var ordered = new HashSet<string>(sales.Skip(1).Select(l => SplitCsv(l)[key]), StringComparer.Ordinal);
                var latest = rows.Where(r => ordered.Contains(r[At("DateKey")])).Max(Day);
                return new ResultSet { Columns = new[] { new ColumnDef { Name = "[Latest]" } }, Rows = new[] { new object[] { latest } }, RowCount = 1 };
            }
            if (q.Contains("'Date'[Date] IN {", StringComparison.Ordinal))
            {
                var asked = Regex.Matches(q.Substring(q.IndexOf("ALL (", StringComparison.Ordinal)), @"'Date'\[([^\]]+)\]").Select(m => m.Groups[1].Value).Distinct().ToList();
                var days = Regex.Matches(q, @"DATE \( (\d+), (\d+), (\d+) \)").Select(m => new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))).ToList();
                object Cell(string[] r, string col) => col == "Date" ? Day(r)
                    : cat.Column("Date[" + col + "]")?.Kind == "whole" ? long.Parse(r[At(col)], CultureInfo.InvariantCulture) : (object)r[At(col)];
                var hit = rows.Where(r => days.Contains(Day(r))).Select(r => asked.Select(c => Cell(r, c)).ToArray()).ToArray();
                return new ResultSet { Columns = asked.Select(c => new ColumnDef { Name = "Date[" + c + "]" }).ToArray(), Rows = hit, RowCount = hit.Length };
            }
            return null;
        };

        private static string[] SplitCsv(string line)
        {
            var cells = new List<string>();
            var cur = new System.Text.StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else quoted = false; } else cur.Append(c); }
                else if (c == '"') quoted = true;
                else if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            cells.Add(cur.ToString());
            return cells.ToArray();
        }

        // ---- the labels line up with the models -----------------------------------------------------------------

        private static IEnumerable<string> LabelledNames(JsonElement expect, string what)
        {
            if (expect.ValueKind != JsonValueKind.Object) yield break;
            if (what == "column")
            {
                foreach (var slot in expect.GetProperty("groupBy").EnumerateArray()) foreach (var c in slot.EnumerateArray()) yield return c.GetString();
                foreach (var f in expect.GetProperty("filters").EnumerateArray()) foreach (var c in f.GetProperty("column").EnumerateArray()) yield return c.GetString();
            }
            else
            {
                foreach (var slot in expect.GetProperty("measures").EnumerateArray()) foreach (var m in slot.EnumerateArray()) yield return m.GetString();
                if (expect.GetProperty("top").ValueKind == JsonValueKind.Object)
                    foreach (var m in expect.GetProperty("top").GetProperty("by").EnumerateArray()) yield return m.GetString();
            }
        }

        /// <summary>Not live: the Contoso labels name only columns and measures the Contoso model has, every labelled filter
        /// value is one of its values, and the set is the size the brief asked for.</summary>
        [Theory]
        [InlineData("labels.json", 55, 70)]
        [InlineData("fresh-labels.json", 75, 85)]
        [InlineData("fresh2-labels.json", 160, 160)]
        [InlineData("fresh3-labels.json", 160, 200)]
        public async Task The_labels_line_up_with_the_Contoso_model(string file, int least, int most)
        {
            using var labels = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "tools", "jev-replay", "query", file)));
            var items = labels.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.InRange(items.Count, least, most);
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await BuildContosoAsync(f.Engine);
            var cat = await f.Sessions.Require().ReadAsync(m => QueryCatalog.Read(m));
            // The AI-ready twin has the same tables, columns and measures, so one catalog checks both arms.
            foreach (var item in items.Where(i => IsContoso(i.GetProperty("model").GetString())))
            {
                var id = item.GetProperty("id").GetString();
                var expect = item.GetProperty("expect");
                foreach (var c in LabelledNames(expect, "column")) Assert.True(cat.Column(c) != null, id + " labels a column the model does not have: " + c);
                foreach (var m in LabelledNames(expect, "measure")) Assert.True(cat.Measure(m) != null, id + " labels a measure the model does not have: " + m);
                if (expect.ValueKind == JsonValueKind.Object)
                    foreach (var fl in expect.GetProperty("filters").EnumerateArray())
                    {
                        var col = fl.GetProperty("column")[0].GetString();
                        var op = fl.GetProperty("op").GetString();
                        foreach (var v in fl.GetProperty("values").EnumerateArray())
                            if (!QueryOps.Compares(op))
                                Assert.True(ContosoValues[col].Any(x => string.Equals(Convert.ToString(x, CultureInfo.InvariantCulture), v.GetString(), StringComparison.Ordinal)), id + " filters on a value the data does not hold: " + v);
                    }
            }
        }

        // ---- the live replay ----------------------------------------------------------------------------------

        private static object Parts(DataAsk p)
        {
            if (p == null) return null;
            string Id(string dax) => QueryTemplate.TryParseColumn(dax, out var t, out var c) ? t + "[" + c + "]" : dax;
            return new
            {
                measures = p.Measures ?? Array.Empty<string>(),
                groupBy = (p.GroupBy ?? Array.Empty<string>()).Select(Id).ToArray(),
                filters = (p.Filters ?? Array.Empty<DataAskFilter>()).Select(x => new { column = Id(x.Column), op = x.Op, values = x.Values }).ToArray(),
                top = p.Top.HasValue && p.Top.Value > 0 ? new { n = p.Top.Value, by = p.TopBy, lowest = p.Lowest } : null,
            };
        }

        /// <summary>The engine's guards on a word-match reading (G1 and the calendar's and twin columns' reasons; the word
        /// check is retired). The word match keeps its filters by candidate only, so they are put on its query first, the
        /// way the engine does before it guards a reading Jev settled.</summary>
        private static bool ArmCovered(string words, JevQueryReader.Reading r, QueryCatalog cat)
        {
            foreach (var (c, op) in r.Filters)
                if (!r.Spec.Filters.Any(f => f.Column == c.Column && f.Op == op)) r.Spec.Filters.Add(new QueryFilter { Column = c.Column, Op = op, Values = c.Values.ToList() });
            return !QueryGuards.G1(r, cat, words).Any() && QueryGuards.Check(words, r, cat, null).Count == 0;
        }

        private static object WordParts(JevQueryReader.Reading r)
        {
            var spec = r.Spec;
            var filters = new List<object>();
            foreach (var g in r.Filters.GroupBy(x => (x.Candidate.Column.Id, x.Op)))
                filters.Add(new { column = g.Key.Id, op = g.Key.Op, values = g.SelectMany(x => x.Candidate.Values).Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)).Distinct().ToArray() });
            return new
            {
                measures = spec.Measures.Select(m => m.Name).ToArray(),
                groupBy = spec.GroupBy.Select(c => c.Id).ToArray(),
                filters = filters.ToArray(),
                top = spec.Top.HasValue ? new { n = spec.Top.Value, by = (spec.TopBy ?? spec.Measures.FirstOrDefault())?.Name, lowest = spec.Lowest } : null,
            };
        }

        /// <summary>Passes every ask to the real client and keeps each answer's picks and confidences, so the result can say
        /// what Jev chose below the bar as well as what the engine took. Test-only; the product never keeps these.</summary>
        private sealed class Keeping : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            public readonly List<Dictionary<string, object>> Last = new List<Dictionary<string, object>>();
            public Keeping(IDecisionOracle inner) { _inner = inner; }
            public bool Available => _inner.Available;
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, System.Threading.CancellationToken ct)
            {
                var a = await _inner.AskAsync(request, ct).ConfigureAwait(false);
                var picks = new Dictionary<string, object>(StringComparer.Ordinal);
                if (a.Decided)
                    foreach (var q in request.Questions)
                    {
                        var x = a.For(q.Name);
                        // The check's gap keeps every probability: the pilot reads P(nothing) and the runner-up from it.
                        var keep = q.Name == JevQueryReader.CheckGapQ ? q.Options.Count : 5;
                        if (x != null) picks[q.Name] = new
                        {
                            pick = x.OptionId, confidence = Math.Round(x.Confidence, 3), options = q.Options.Count,
                            probs = x.Probabilities.OrderByDescending(kv => kv.Value).Take(keep).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3)),
                        };
                    }
                else picks["notDecided"] = a.NotDecidedReason;
                // The reading the check judged, told in the question's own words (the field that leaves the machine).
                if (request.Snapshot.TryGetValue("stated_reading", out var stated)) picks["stated_reading"] = stated;
                lock (Last) Last.Add(picks);
                return a;
            }
        }

        [JevReplayFact]
        public async Task Replay_the_labelled_questions_through_the_product()
        {
            var repo = Repo();
            var outDir = Environment.GetEnvironmentVariable("JEV_REPLAY_OUT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "jev-replay-query");
            if (Path.GetFullPath(outDir).StartsWith(Path.GetFullPath(repo), StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to write results inside the repository.");
            Directory.CreateDirectory(outDir);
            var fixtures = Environment.GetEnvironmentVariable("JEV_REPLAY_FIXTURES");
            if (string.IsNullOrWhiteSpace(fixtures) || !Directory.Exists(Path.Combine(fixtures, "northstar")) || !Directory.Exists(Path.Combine(fixtures, "data")))
                throw new InvalidOperationException("Set JEV_REPLAY_FIXTURES to the UAT fixtures folder, the one that holds northstar/ and data/.");
            using var labels = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "tools", "jev-replay", "query", LabelFile())));
            var items = labels.RootElement.GetProperty("items").EnumerateArray().ToList();

            var budget = new DecisionBudget(2_000_000);
            var rows = new List<object>();
            foreach (var model in new[] { "northstar", "contoso", "contoso-ai" })
            {
                // A set that names no question on this model does not open it.
                if (!items.Any(i => i.GetProperty("model").GetString() == model)) continue;
                using var f = new DecisionFixture(TestEntitlements.Pro);
                Func<string, string, object[]> distinct;
                Func<string, ResultSet> calendar = null;
                if (model == "northstar")
                {
                    var scratch = Path.Combine(f.Home, "northstar");
                    CopyDir(Path.Combine(fixtures, "northstar"), scratch);
                    var definition = Path.Combine(scratch, "Northstar.SemanticModel", "definition");
                    if (Bare) StripNorthstar(definition);
                    await f.Engine.OpenAsync(definition);
                    if (Bare) await AssertBareNorthstarAsync(f); else await AssertNorthstarArmAsync(f);
                    var cat0 = await f.Sessions.Require().ReadAsync(m => QueryCatalog.Read(m));
                    distinct = CsvValues(Path.Combine(fixtures, "data"), cat0);
                    calendar = CsvCalendar(Path.Combine(fixtures, "data"), cat0);
                }
                else
                {
                    if (model == "contoso-ai") await BuildContosoAiAsync(f.Engine);
                    else await BuildContosoAsync(f.Engine);
                    await f.SaveAsAsync("ContosoSales.bim");
                    distinct = (t, c) => ContosoValues.TryGetValue(t + "[" + c + "]", out var v) ? v : Array.Empty<object>();
                }
                await f.ConsentAsync();
                f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Replay " + model, model, Answering(distinct, calendar)));
                f.Engine.JevReadsDataForTest = true;
                using var jev = new JevDecisionOracle(Key(), budget);
                // With JEV_REPLAY_PICKS the parts come from a recorded run and only the check is live (Recorded.cs).
                var recordedPicks = Environment.GetEnvironmentVariable("JEV_REPLAY_PICKS") is { Length: > 0 } picksPath ? RecordedPicks(picksPath) : null;
                var parts = recordedPicks == null ? null : new RecordedPartsLiveCheck(jev);
                var keeping = new Keeping(parts == null ? (IDecisionOracle)jev : parts);
                f.Engine.UseDecisionsForTest(new DecisionService(keeping, budget));
                long checkMs = -1;
                f.Engine.CheckTimedForTest = ms => checkMs = ms;
                var cat = await f.Sessions.Require().ReadAsync(m => QueryCatalog.Read(m));

                // Every labelled column and measure exists in this model, before anything is asked.
                foreach (var item in items.Where(i => i.GetProperty("model").GetString() == model))
                {
                    foreach (var c in LabelledNames(item.GetProperty("expect"), "column")) Assert.True(cat.Column(c) != null, item.GetProperty("id").GetString() + ": " + c);
                    foreach (var m in LabelledNames(item.GetProperty("expect"), "measure")) Assert.True(cat.Measure(m) != null, item.GetProperty("id").GetString() + ": " + m);
                }
                // The word arm sees what the engine sees: a column with more values than the engine reads is left out.
                var values = ValueMatcher.Readable(cat).Select(c => (c.Id, v: distinct(c.Table, c.Name)))
                    .Where(x => x.v.Length <= ValueMatcher.MaxValuesPerColumn).ToDictionary(x => x.Id, x => x.v);

                foreach (var item in items.Where(i => i.GetProperty("model").GetString() == model))
                {
                    var id = item.GetProperty("id").GetString();
                    var words = item.GetProperty("words").GetString();
                    var started = DateTime.UtcNow;
                    DataAnswer a = null;
                    string error = null;
                    lock (keeping.Last) keeping.Last.Clear();
                    checkMs = -1;
                    parts?.Load(recordedPicks.TryGetValue(id, out var recordedFor) ? recordedFor : new List<JsonElement>());
                    try { a = await f.Engine.AskDataAsync(new DataAsk { Question = words, Run = false }); }
                    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                    var ms = (DateTime.UtcNow - started).TotalMilliseconds;
                    var candidates = ValueMatcher.Match(words, cat, values);
                    var arm = JevQueryWordArm.Read(words, cat, candidates);
                    var shortArm = JevQueryWordArm.ReadShortlist(words, cat, candidates);
                    rows.Add(new
                    {
                        id, model, words,
                        jev = a == null ? null : new
                        {
                            outcome = a.Outcome, readBy = a.ReadBy, reading = a.Reading, statement = a.Statement, note = a.Note, unsure = a.Unsure,
                            others = a.Others.Select(o => new { line = o.Line, reading = o.Reading, parts = Parts(o.Parts) }).ToArray(),
                            failed = a.Checks.Where(c => !c.Passed).Select(c => c.Name + ": " + c.Line).ToArray(),
                            parts = Parts(a.Parts),
                        },
                        error,
                        picks = keeping.Last.ToList(),
                        ms,
                        candidates = candidates.Count,
                        // The rules pass the same guards the product does (round 4): a reading a guard stops is undecided,
                        // as it leaves the product's answer needing you.
                        words_arm = new { decided = arm.Spec.Measures.Count > 0 && arm.Unsure.Count == 0 && ArmCovered(words, arm, cat), parts = WordParts(arm) },
                        shortlist_arm = new { decided = shortArm.Spec.Measures.Count > 0 && shortArm.Unsure.Count == 0 && ArmCovered(words, shortArm, cat), parts = WordParts(shortArm) },
                        words_arm_before_coverage = arm.Spec.Measures.Count > 0 && arm.Unsure.Count == 0,
                        shortlist_arm_before_coverage = shortArm.Spec.Measures.Count > 0 && shortArm.Unsure.Count == 0,
                        requests = keeping.Last.Count,
                        checkMs,
                        mismatches = parts?.Mismatches.ToArray(),
                    });
                }
            }

            var result = new
            {
                ranUtc = DateTime.UtcNow.ToString("o"),
                set = LabelFile(),
                bare = Bare,
                point = DecisionRegistry.QuerySummarize,
                requests = budget.Requests,
                inputTokens = budget.UsedInputTokens,
                rows,
            };
            File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(rows.Count > 0);
        }

        /// <summary>Both Contoso arms: the raw model and its AI-ready twin, which hold the same things.</summary>
        private static bool IsContoso(string model) => model == "contoso" || model == "contoso-ai";

        // ---- what each arm really holds ------------------------------------------------------------------------

        /// <summary>What the Northstar UAT fixture carries, read through the engine from northstar-pristine.tgz (sha256
        /// 19ecf421...), the fixture every replay opened. It is NOT a raw model: it describes 4 of its 10 visible measures,
        /// 6 of its visible columns and 3 of its tables, and gives one table synonyms. The fresh3 record first called it
        /// raw ("no descriptions and no synonyms"), from a grep for "description" in its TMDL, which counts nothing
        /// because TMDL writes a description as /// lines.</summary>
        internal static readonly string[] NorthstarDescribedMeasures = { "Margin %", "Net Sales", "Total Cost", "Units" };
        internal static readonly string[] NorthstarDescribedColumns =
            { "Customer[Segment]", "Date[Date]", "Date[YearMonth]", "Product[Category]", "Sales[NetSalesAmount]", "Sales[OrderNumber]" };
        internal static readonly string[] NorthstarDescribedTables = { "Date", "Sales", "Store" };

        /// <summary>Asserts the opened Northstar model holds exactly that, so a replay can never again describe its
        /// Northstar arm as something it is not, and a different fixture cannot be scored under the same name.</summary>
        private static async Task AssertNorthstarArmAsync(DecisionFixture f)
        {
            var cat = await f.Sessions.Require().ReadAsync(m => QueryCatalog.Read(m));
            static string[] Sorted(IEnumerable<string> xs) => xs.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(10, cat.VisibleMeasures.Count());
            Assert.Equal(NorthstarDescribedMeasures, Sorted(cat.VisibleMeasures.Where(m => !string.IsNullOrWhiteSpace(m.Description)).Select(m => m.Name)));
            Assert.Equal(51, cat.Visible.Count());
            Assert.Equal(NorthstarDescribedColumns, Sorted(cat.Visible.Where(c => !string.IsNullOrWhiteSpace(c.Description)).Select(c => c.Id)));
            var tables = await f.Sessions.Require().ReadAsync(m => Sorted(m.Tables.Where(t => !string.IsNullOrWhiteSpace(t.Description)).Select(t => t.Name)));
            Assert.Equal(NorthstarDescribedTables, tables);
            // One table with synonyms: "sales" (Generated) and "transactions" (Authored), in cultures/en-US.tmdl.
            Assert.Equal(new[] { "Sales" }, Sorted(cat.TableSynonyms.Keys));
            Assert.Equal(new[] { "sales", "transactions" }, Sorted(cat.TableSynonyms["Sales"].Select(t => t.ToLowerInvariant())));
            Assert.All(cat.Measures, m => Assert.Empty(m.Synonyms));
            Assert.All(cat.Columns, c => Assert.Empty(c.Synonyms));
        }

        /// <summary>The Northstar fixture is partly described (above), so the natural set, written for a raw model, is
        /// asked of a copy with every description (TMDL's "///" lines) and every culture (the synonyms) taken out. The
        /// copy only: the fixture is never written.</summary>
        internal static void StripNorthstar(string definition)
        {
            foreach (var file in Directory.GetFiles(definition, "*.tmdl", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                var kept = lines.Where(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)).ToArray();
                if (kept.Length != lines.Length) File.WriteAllLines(file, kept);
            }
            var cultures = Path.Combine(definition, "cultures");
            if (Directory.Exists(cultures)) foreach (var c in Directory.GetFiles(cultures)) File.Delete(c);
        }

        /// <summary>The stripped copy carries no description and no synonym anywhere, and still shows every measure and
        /// column the described copy shows.</summary>
        private static async Task AssertBareNorthstarAsync(DecisionFixture f)
        {
            var cat = await f.Sessions.Require().ReadAsync(m => QueryCatalog.Read(m));
            Assert.Equal(10, cat.VisibleMeasures.Count());
            Assert.Equal(51, cat.Visible.Count());
            Assert.All(cat.Measures, m => { Assert.True(string.IsNullOrWhiteSpace(m.Description), m.Name + " is described"); Assert.Empty(m.Synonyms); });
            Assert.All(cat.Columns, c => { Assert.True(string.IsNullOrWhiteSpace(c.Description), c.Id + " is described"); Assert.Empty(c.Synonyms); });
            Assert.Empty(cat.TableSynonyms);
            Assert.Empty(await f.Sessions.Require().ReadAsync(m => m.Tables.Where(t => !string.IsNullOrWhiteSpace(t.Description)).Select(t => t.Name).ToList()));
        }

        /// <summary>Not live, but it needs the fixture: the natural set's Northstar arm really is bare once stripped.</summary>
        [ReplayFixturesFact]
        public async Task The_stripped_Northstar_copy_carries_no_description_and_no_synonym()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var scratch = Path.Combine(f.Home, "northstar");
            CopyDir(Path.Combine(Environment.GetEnvironmentVariable("JEV_REPLAY_FIXTURES"), "northstar"), scratch);
            var definition = Path.Combine(scratch, "Northstar.SemanticModel", "definition");
            StripNorthstar(definition);
            await f.Engine.OpenAsync(definition);
            await AssertBareNorthstarAsync(f);
        }

        /// <summary>Not live, but it needs the fixture: JEV_REPLAY_FIXTURES names the folder holding northstar/ (the
        /// README says how to assemble it). Without it the fact is reported SKIPPED, never passed.</summary>
        [ReplayFixturesFact]
        public async Task The_Northstar_replay_model_is_partly_described_and_names_one_table_synonym()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var scratch = Path.Combine(f.Home, "northstar");
            CopyDir(Path.Combine(Environment.GetEnvironmentVariable("JEV_REPLAY_FIXTURES"), "northstar"), scratch);
            await f.Engine.OpenAsync(Path.Combine(scratch, "Northstar.SemanticModel", "definition"));
            await AssertNorthstarArmAsync(f);
        }

        /// <summary>Not live: the AI-ready arm really is prepared, and the plain Contoso arm the older sets were scored on
        /// really is bare. It opens both Contoso models and proves nothing about Northstar; that is the fact above.</summary>
        [Fact]
        public async Task The_AI_ready_replay_model_carries_descriptions_and_synonyms()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await BuildContosoAiAsync(f.Engine);
            var cat = await f.Sessions.Require().ReadAsync(m => QueryCatalog.Read(m));
            Assert.All(cat.VisibleMeasures, m => Assert.False(string.IsNullOrWhiteSpace(m.Description), m.Name + " has no description"));
            Assert.All(cat.Visible.Where(c => !c.Key), c => Assert.False(string.IsNullOrWhiteSpace(c.Description), c.Id + " has no description"));
            Assert.Contains(cat.VisibleMeasures, m => m.Name == "Total Revenue" && m.Synonyms.Contains("revenue", StringComparer.OrdinalIgnoreCase));
            Assert.Contains(cat.Visible, c => c.Id == "Product[Color]" && c.Synonyms.Contains("colour", StringComparer.OrdinalIgnoreCase));
            Assert.True(cat.TableSynonyms.ContainsKey("Geography"), "the Geography table has no synonyms");

            // And the plain Contoso model is bare, so the two Contoso arms differ only in that metadata. (It is not in
            // fresh3; the older sets were scored on it.)
            using var raw = new DecisionFixture(TestEntitlements.Pro);
            await BuildContosoAsync(raw.Engine);
            var rawCat = await raw.Sessions.Require().ReadAsync(m => QueryCatalog.Read(m));
            Assert.All(rawCat.VisibleMeasures, m => Assert.True(string.IsNullOrWhiteSpace(m.Description), m.Name + " is described on the raw arm"));
            Assert.Empty(rawCat.TableSynonyms);
        }

        private static void CopyDir(string from, string to)
        {
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            {
                if (dir.Contains(Path.DirectorySeparatorChar + ".git")) continue;
                Directory.CreateDirectory(dir.Replace(from, to));
            }
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)) continue;
                File.Copy(file, file.Replace(from, to), true);
            }
        }
    }
}
