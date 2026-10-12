using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verifier round 6 of m11/sql-start (at 2f75a1d2): the blind held-out set Sol wrote for this round, run
    /// once (/tmp/semanticus-joint-review/lane-reports/v-sql-start-r6.md). Sol's files sit in
    /// Semanticus.VSCode/test/sql-start-heldout-r6/ exactly as Sol wrote them (their sha256 are in the report), labels
    /// and all; nothing here changes a label. The tests hold the lane to the coordinator's stop rule for that set:
    /// (a) no time measure over a date table its fact does not reach, and no wrong mark; (b) no ask about a model that
    /// exists opens the Model Spec, and at least 27 of the 30 asks to make a model from a SQL source do. The mark test
    /// and the 27-of-30 test fail at 2f75a1d2; the other two are guards that pass. The bar's half of (b) is in
    /// Semanticus.VSCode/test/sql-start-verify-r6.test.mjs.</summary>
    public sealed class SqlDraftVerifyR6Tests
    {
        private static readonly string[] Variants = { "YoYPct", "YTD", "QTD", "MTD", "PY", "YoY" };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static string HeldOut(params string[] parts) =>
            Path.Combine(new[] { RepoRoot(), "Semanticus.VSCode", "test", "sql-start-heldout-r6" }.Concat(parts).ToArray());

        private static JsonElement Label(string id)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(HeldOut("labels.json")));
            return doc.RootElement.EnumerateArray().Single(l => l.GetProperty("id").GetString() == id).Clone();
        }

        public static IEnumerable<object[]> Schemas()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(HeldOut("labels.json")));
            foreach (var l in doc.RootElement.EnumerateArray()) yield return new object[] { l.GetProperty("id").GetString()! };
        }

        /// <summary>Draft the schema with the one draft function both doors call (the MCP tool and the Studio's
        /// Autogenerate from SQL both end in FabricSchemaToSpec), build it, and read the BUILT model: the tables marked as
        /// date tables, and for each time measure the fact its base measure sums, the date table its DAX filters by, and
        /// whether that date table reaches the fact through active relationships.</summary>
        private static async Task<(string[] Marked, (string Measure, string Fact, string Date, bool Reaches)[] Time, string Note)> Build(string id)
        {
            var schema = JsonSerializer.Deserialize<FabricSchema>(File.ReadAllText(HeldOut("schemas", id + ".json")))!;
            var spec = LocalEngine.FabricSchemaToSpec(schema, schema.Server, schema.Database, "import");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync(id, 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(spec), "human");
            var report = await engine.BuildModelFromSpecAsync("human");
            Assert.True(report.Errors.Length == 0, $"{id}: the draft builds with errors: {string.Join(" | ", report.Errors)}");
            return await sessions.Current.ReadAsync(m =>
            {
                var marked = m.Tables.Where(t => t.DataCategory == "Time").Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                var time = m.AllMeasures.Where(x => x.DisplayFolder == "Time Intelligence").Select(x =>
                {
                    var baseName = Variants.Where(v => x.Name.EndsWith(" " + v, StringComparison.Ordinal)).Select(v => x.Name.Substring(0, x.Name.Length - v.Length - 1)).FirstOrDefault();
                    var fact = m.AllMeasures.FirstOrDefault(b => b.Name == baseName)?.Table;
                    var dateName = Regex.Matches(x.Expression ?? "", @"'((?:[^']|'')+)'\s*\[").Select(g => g.Groups[1].Value.Replace("''", "'")).LastOrDefault();
                    var date = dateName == null ? null : m.Tables.FirstOrDefault(t => t.Name == dateName);
                    return (x.Name, fact?.Name, dateName, Reaches(m, date, fact));
                }).ToArray();
                return (marked, time, spec.DateTable?.Note);
            });
        }

        // A filter on `from` reaches `to` along active relationships: one side to many side, and both ways where a link
        // filters both ways.
        private static bool Reaches(Model m, Table? from, Table? to)
        {
            if (from == null || to == null) return false;
            var seen = new HashSet<Table> { from };
            var queue = new Queue<Table>(new[] { from });
            while (queue.Count > 0)
            {
                var at = queue.Dequeue();
                if (at == to) return true;
                foreach (var r in m.Relationships.OfType<SingleColumnRelationship>().Where(r => r.IsActive))
                {
                    var next = r.ToTable == at ? r.FromTable
                             : r.FromTable == at && r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections ? r.ToTable : null;
                    if (next != null && seen.Add(next)) queue.Enqueue(next);
                }
            }
            return false;
        }

        /// <summary>Stop rule (a), the marks. Sol labelled each source with the table that should be marked as the date
        /// table, the others that would be just as right, or none. At 2f75a1d2, 8 of the 15 are marked wrong, on both doors:
        /// a date table named for its grain (dim_day, dim_billing_day, dim_trade_day: a date column, a key, and the facts'
        /// own active links) is not seen as a date table, so a CALENDAR table called Date is added beside it and MARKED,
        /// though no fact links to it; and a source with no date table at all (S08) gets the same unlinked Date marked.
        /// A marked date table that nothing filters by is the harm Sol was asked to find (H2), and it is the table any
        /// time calculation written later, by a person or by Copilot, is written against.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Schemas))]
        public async Task A_held_out_source_marks_the_date_table_its_facts_link_to_and_nothing_else(string id)
        {
            var label = Label(id);
            var built = await Build(id);
            var want = label.GetProperty("date_table").ValueKind == JsonValueKind.Null ? null : label.GetProperty("date_table").GetString();
            var alsoRight = label.GetProperty("also_acceptable_date_tables").EnumerateArray().Select(x => x.GetString()).ToArray();

            if (want == null)
                Assert.True(built.Marked.Length == 0,
                    $"{id}: Sol labels no date table ({label.GetProperty("shape").GetString()}), yet the built model marks [{string.Join(", ", built.Marked)}]. Note: {built.Note}");
            else
                Assert.True(built.Marked.Length == 1 && (built.Marked[0] == want || alsoRight.Contains(built.Marked[0])),
                    $"{id}: Sol labels {want}{(alsoRight.Length > 0 ? " (or " + string.Join(", ", alsoRight) + ")" : "")} as the date table, and the built model marks [{string.Join(", ", built.Marked)}]. Note: {built.Note}");
        }

        /// <summary>Stop rule (a), the numbers: a guard, and it passes at 2f75a1d2 on all 15. No time measure in any held-out
        /// build runs over a date table that does not reach its fact.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Schemas))]
        public async Task No_held_out_time_measure_runs_over_a_date_table_its_fact_does_not_reach(string id)
        {
            var built = await Build(id);
            var wrong = built.Time.Where(t => !t.Reaches).Select(t => $"{t.Measure} (on {t.Fact}, over {t.Date})").ToArray();
            Assert.True(wrong.Length == 0, $"{id}: time measures over a date table that does not reach their fact: {string.Join("; ", wrong)}");
        }

        private static async Task<(string Id, string Text, string Label, string? Tool)[]> RouteSolsAsks()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(HeldOut("asks.json")));
            using var f = new DecisionFixture();
            await f.BuildModelAsync("SqlStartHeldOutR6.bim");
            f.UseNull();
            var rows = new List<(string, string, string, string?)>();
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                var text = a.GetProperty("text").GetString()!;
                var result = await f.Engine.RouteAskAsync(text);
                rows.Add((a.GetProperty("id").GetString()!, text, a.GetProperty("label").GetString()!, result.Tool));
            }
            return rows.ToArray();
        }

        /// <summary>Stop rule (b), the first half: a guard, and it passes at 2f75a1d2. None of Sol's 30 asks about a model
        /// that already exists opens the Model Spec (route_ask and the RPC routeAsk both call RouteAskAsync).</summary>
        [Fact]
        public async Task No_held_out_ask_about_a_model_that_exists_opens_the_model_spec()
        {
            var rows = await RouteSolsAsks();
            var existing = rows.Where(r => r.Label == "existing").ToArray();
            Assert.Equal(30, existing.Length);
            var spec = existing.Where(r => r.Tool == "spec").Select(r => $"{r.Id} \"{r.Text}\"").ToArray();
            Assert.True(spec.Length == 0, $"asks about a model that exists routed to the Model Spec: {string.Join("; ", spec)}");
        }

        /// <summary>Stop rule (b), the second half. At least 27 of Sol's 30 everyday asks to make a model from a SQL source
        /// open the Model Spec. At 2f75a1d2, 8 do. The route needs a making verb, "model", from, out of or using, then the
        /// word "sql" and nothing after it but the source's name, so "make a new model from the data warehouse", "build me
        /// a model from the database schema", "new dataset from the SQL source please", "set up a fresh model using SQL
        /// tables" and "connect to SQL and create a model" all miss. The decision service does not rescue them either
        /// (informational run with it on: still 8).</summary>
        [Fact]
        public async Task At_least_27_of_the_30_held_out_asks_to_make_a_model_from_sql_open_the_model_spec()
        {
            var rows = await RouteSolsAsks();
            var make = rows.Where(r => r.Label == "make").ToArray();
            Assert.Equal(30, make.Length);
            var misses = make.Where(r => r.Tool != "spec").Select(r => $"{r.Id} \"{r.Text}\" ({r.Tool ?? "no place"})").ToArray();
            Assert.True(make.Length - misses.Length >= 27,
                $"{make.Length - misses.Length} of 30 asks to make a model from SQL open the Model Spec; the stop rule is 27. Missed: {string.Join("; ", misses)}");
        }
    }
}
