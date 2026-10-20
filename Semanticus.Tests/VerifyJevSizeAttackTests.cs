// Verifier attacks on m16/jev-size (lane v-jev-size, 15 Oct 2026). Each test tries to break one claim of the lane: every batched question's
// words name the object its answer is written to (same names in many tables, quotes, brackets, unicode, names that look like a path or a
// list entry), and each point's requests keep to the point's own bound while small tables share a request and a big table splits. A test
// that fails here proves a defect; it is not a fix.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyJevSizeAttackTests
    {
        // ---- a relay that keeps every request and can choose by the WORDS of a question, not its name -----------------------------

        private sealed class WordsRelay : IDecisionOracle
        {
            public Func<DecisionQuestion, string> Choose;
            public readonly List<DecisionRequest> Seen = new List<DecisionRequest>();
            public bool Available => true;

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (Seen) Seen.Add(request);
                var parts = new List<string>();
                foreach (var q in request.Questions)
                {
                    string v;
                    if (q.Kind == DecisionQuestionKind.Choice) v = "{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(Choose?.Invoke(q) ?? q.Options[0].Id) + ",\"confidence\":0.95}";
                    else if (q.Kind == DecisionQuestionKind.Noul) v = "{\"type\":\"noul\",\"noul\":0.9}";
                    else
                    {
                        var legend = string.Join(",", q.Options.Select((o, i) => "\"" + i + "\":" + JsonSerializer.Serialize(o.Criterion)));
                        var probs = string.Join(",", q.Options.Select((o, i) => "\"" + i + "\":" + (i == 2 ? "0.9" : "0.025")));
                        v = "{\"type\":\"score\",\"score\":2.0,\"confidence\":0.9,\"legend\":{" + legend + "},\"probabilities\":{" + probs + "}}";
                    }
                    parts.Add(JsonSerializer.Serialize(q.Name) + ":" + v);
                }
                using var doc = JsonDocument.Parse("{" + string.Join(",", parts) + "}");
                return Task.FromResult(DecisionAnswerReader.Read(request, doc.RootElement.Clone(), "jev-test-1", 100, 5));
            }
        }

        private static async Task<DecisionFixture> OpenAsync(int tables, IDecisionOracle relay, Action<JsonNode> grow = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "vjs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "many.bim");
            ManyTablesModel.Write(bim, tables);
            if (grow != null)
            {
                var node = JsonNode.Parse(File.ReadAllText(bim));
                grow(node);
                File.WriteAllText(bim, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.OpenAsync(bim);
            await f.SaveAsAsync("many.bim");
            await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new DecisionService(relay, new DecisionBudget(), answers: new DecisionAnswerStore()));
            f.Engine.JevBatchedForTest = _ => true;
            return f;
        }

        private static async Task<ModelPlanView> ReadUntilQuietAsync(DecisionFixture f, Func<int> calls)
        {
            var seen = -1;
            for (var read = 0; read < 12; read++)
            {
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                if (calls() == seen) break;
                seen = calls();
            }
            return await f.Engine.GetModelPlanAsync();
        }

        private static int Hash(string s) { var h = 17; foreach (var c in s) h = unchecked(h * 31 + c); return h & 0x7fffffff; }

        private static readonly Dictionary<string, (string List, string Noun)> Lists = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["ai.schema"] = ("objects", "object"),
            ["measure.folder_and_format"] = ("measures", "measure"),
            ["column.summarize"] = ("columns", "column"),
            ["column.format"] = ("columns", "column"),
        };

        // The words' own claim: path index, name and table. The name and table may hold quotes, so the split is on the fixed joiners.
        private static readonly Regex LeadPattern = new Regex("^For the (object|measure|column) `(objects|measures|columns)\\[(\\d+)\\]`, named \"(.*)\" in the table \"(.*?)\": ", RegexOptions.Singleline);

        private static (int Index, string Name, string Table) Words(DecisionQuestion q)
        {
            var m = LeadPattern.Match(q.Instructions);
            Assert.True(m.Success, "a batched question does not open with the lead: " + q.Instructions.Substring(0, Math.Min(160, q.Instructions.Length)));
            return (int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[4].Value, m.Groups[5].Value);
        }

        // The hostile names. Columns and measures first (their refs still use the old table names), then the tables.
        private static readonly (string Ref, string Name)[] Renames =
        {
            ("column:Activity Event/Cost", "Cost \"net\" [EUR]"),                         // quotes and brackets, and its twins "Cost" stay in two other tables
            ("column:Refresh History/Duration Ms", "columns[0]"),                         // a column named like an entry of the list
            ("column:Capacity Operation/Item Key", "Item \"Key\" (id) \u58f2"),             // a key column that gets a base measure; its twins keep the old name (plain: the DAX check refuses a base measure over a column named with a quote or unicode, an older limit, see the report)
            ("column:Item/Category", "Kategorie 売上 \U0001F642"),                  // unicode and an emoji (a surrogate pair)
            ("measure:Activity Event/Activity Event Count", "Zählung \"Ereignisse\" [n]"),
            ("measure:Capacity Operation/Capacity Operation Row Count", "objects[3]"),
            ("table:Workspace", "Data\\Sales \"Q1\" columns[2]"),                          // a table that looks like a path and a list entry
            ("table:Hour", "measures[1]"),
        };

        // ---- 1. the words name the object the answer is written to ----------------------------------------------------------------

        [Fact]
        public async Task Attack_every_batched_question_names_the_object_its_answer_is_written_to_even_with_hostile_names()
        {
            var aggs = new[] { "max", "min" };   // a count over a key is guarded to the rule (Kane, 11 Oct), so only picks the key columns keep
            var relay = new WordsRelay
            {
                // Jev reads the WORDS: the pick is made from the name and table in the question's opening, never from the question's name.
                Choose = q =>
                {
                    if (!q.Name.StartsWith("summarize_by:", StringComparison.Ordinal)) return q.Options[0].Id;
                    var w = Words(q);
                    return aggs[Hash(w.Table + "|" + w.Name) % aggs.Length];
                },
            };
            using var f = await OpenAsync(12, relay);
            foreach (var (objRef, name) in Renames)
            {
                var r = await f.Engine.RenameObjectAsync(objRef, name, "human");
                Assert.True(r.Changed, "rename of " + objRef + " did not change the model");
            }
            var plan = await ReadUntilQuietAsync(f, () => relay.Seen.Count);

            var checkedQuestions = 0;
            var hostileSeen = new HashSet<string>(StringComparer.Ordinal);
            var bad = new List<string>();
            foreach (var r in relay.Seen.Where(r => Lists.ContainsKey(r.PointName)))
            {
                var (list, noun) = Lists[r.PointName];
                using var d = JsonDocument.Parse(DecisionDigest.CanonicalSnapshotJson(r.Snapshot));
                var entries = d.RootElement.GetProperty(list);
                foreach (var q in r.Questions)
                {
                    var w = Words(q);
                    if (w.Index >= entries.GetArrayLength()) { bad.Add(q.Name + " points past the list"); continue; }
                    var e = entries[w.Index];
                    var eName = e.GetProperty("name").GetString();
                    var eTable = e.GetProperty("table").GetString();
                    // the words, rebuilt exactly from the entry they point at
                    if (!q.Instructions.StartsWith(JevTableBatch.Lead(noun, list, w.Index, eName, eTable), StringComparison.Ordinal))
                        bad.Add("words and entry differ: " + q.Instructions.Substring(0, Math.Min(140, q.Instructions.Length)) + " / entry " + eTable + " :: " + eName);
                    // the object the answer is written to (the ref in the question's name) is the one the words name
                    var objRef = q.Name.Substring(q.Name.IndexOf(':') + 1);
                    var kind = r.PointName == "ai.schema" ? e.GetProperty("kind").GetString() : noun;
                    var expected = kind == "table" ? "table:" + eTable : kind + ":" + eTable + "/" + eName;
                    if (kind == "table" && eName != eTable) bad.Add("a table entry names another table: " + eName + " in " + eTable);
                    if (!string.Equals(objRef, expected, StringComparison.Ordinal)) bad.Add("answer goes to " + objRef + " but the words name " + expected);
                    if (w.Name != eName || w.Table != eTable) bad.Add("parsed words differ from the entry: " + w.Table + " :: " + w.Name);
                    foreach (var (_, hostile) in Renames) if (w.Name == hostile || w.Table == hostile) hostileSeen.Add(hostile);
                    checkedQuestions++;
                }
            }
            Assert.True(bad.Count == 0, bad.Count + " of " + checkedQuestions + " questions: " + string.Join(" ; ", bad.Take(6)));
            Assert.True(checkedQuestions > 150, "too few batched questions to judge: " + checkedQuestions);
            Assert.True(Lists.Keys.All(p => relay.Seen.Any(r => r.PointName == p)), "a point was never asked: " + string.Join(",", relay.Seen.Select(r => r.PointName).Distinct()));
            var missingHostile = Renames.Select(x => x.Name).Where(n => !hostileSeen.Contains(n)).ToList();
            Assert.True(missingHostile.Count == 0, "hostile names never reached a question: " + string.Join(" | ", missingHostile));

            // End to end: each base measure is built from the pick the WORDS led to, for the column the measure is built on.
            var fn = new Dictionary<string, string> { ["max"] = "MAX", ["min"] = "MIN", ["count"] = "COUNT", ["distinctcount"] = "DISTINCTCOUNT" };
            var creates = plan.Items.Where(i => i.Id.StartsWith("jev." + ColumnSummarizeFamily.Name + ".", StringComparison.Ordinal))
                .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Where(l => l.Op == "create_measure").ToList();
            var asked = relay.Seen.Where(r => r.PointName == "column.summarize").SelectMany(r => r.Questions).Select(Words).ToList();
            Assert.Contains(asked, w => w.Name == "Cost \"net\" [EUR]" && w.Table == "Activity Event");
            var wrong = new List<string>();
            var matched = 0;
            foreach (var w in asked)
            {
                var daxRef = "'" + w.Table.Replace("'", "''") + "'[" + w.Name.Replace("]", "]]") + "]";
                var mine = creates.Where(l => (l.After ?? "").Contains("(" + daxRef + ")") || Regex.IsMatch(l.After ?? "", "\\(\\s*" + Regex.Escape(daxRef) + "\\s*\\)")).ToList();
                foreach (var l in mine)
                {
                    matched++;
                    var want = fn[aggs[Hash(w.Table + "|" + w.Name) % aggs.Length]];
                    if (!Regex.IsMatch(l.After ?? "", "^\\s*" + want + "\\s*\\(")) wrong.Add(w.Table + " :: " + w.Name + " wanted " + want + " got " + l.After);
                }
            }
            Assert.True(matched >= 10, "too few base measures matched to their column to judge: " + matched + " of " + creates.Count);
            Assert.True(wrong.Count == 0, wrong.Count + " base measures do not follow the words: " + string.Join(" ; ", wrong.Take(6)));
            // A hostile key column (no measure yet, its twin "Item Key" stays in the other fact tables): the pick its WORDS led to names the base
            // measure the Plan offers for THAT column. After a rename the model's DAX check refuses the formula (an older limit, see the report), so
            // the measure shows on the row's refused list instead of as a line: either way it is the right column with the right pick.
            const string key = "Item \"Key\" (id) \u58f2";
            var keyMeasure = "measure:Capacity Operation/" + (aggs[Hash("Capacity Operation|" + key) % aggs.Length] == "max" ? "Maximum " : "Minimum ") + key;
            var summarizeRows = plan.Items.Where(i => i.Id.StartsWith("jev." + ColumnSummarizeFamily.Name + ".", StringComparison.Ordinal)).ToList();
            var offered = summarizeRows.SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Where(l => l.Op == "create_measure").Select(l => l.Target)
                .Concat(summarizeRows.SelectMany(i => i.ChangeSet?.Refused ?? Array.Empty<ModelPlanChangeRefusal>()).Select(x => x.Target)).Where(t => (t ?? "").Contains(key)).ToList();
            Assert.True(offered.Count == 1 && offered[0] == keyMeasure, "wanted " + keyMeasure + ", got " + string.Join(" ; ", offered));
        }

        // ---- 2. each point keeps its bound; small tables share; a big table splits ----------------------------------------------

        private static void Grow(JsonNode model)
        {
            var tables = model["model"]["tables"].AsArray();
            var ae = tables.First(t => (string)t["name"] == "Activity Event");
            var cols = ae["columns"].AsArray();
            var measures = ae["measures"].AsArray();
            for (var i = 1; i <= 110; i++)
            {
                var name = "Metric " + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
                cols.Add(new JsonObject { ["name"] = name, ["dataType"] = "double", ["sourceColumn"] = name });
                if (i <= 40) measures.Add(new JsonObject { ["name"] = "Activity Event " + name + " Total", ["expression"] = "SUM('Activity Event'[" + name + "])" });
            }
        }

        [Fact]
        public async Task Attack_each_point_keeps_its_bound_small_tables_share_and_a_table_over_the_bound_splits_with_nothing_lost()
        {
            var relay = new WordsRelay();
            using var f = await OpenAsync(30, relay, Grow);
            await ReadUntilQuietAsync(f, () => relay.Seen.Count);

            var bounds = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["ai.schema"] = JevTableBatch.AiSchemaObjectsPerRequest,
                ["measure.folder_and_format"] = JevTableBatch.MeasureObjectsPerRequest,
                ["column.summarize"] = JevTableBatch.ColumnObjectsPerRequest,
                ["column.format"] = JevTableBatch.ColumnObjectsPerRequest,
            };
            static string RefOf(string questionName) => questionName.Substring(questionName.IndexOf(':') + 1);
            static string TableOf(string objRef) { var body = objRef.Substring(objRef.IndexOf(':') + 1); var slash = body.IndexOf('/'); return slash < 0 ? body : body.Substring(0, slash); }
            var report = new List<string>();
            foreach (var (point, bound) in bounds)
            {
                var calls = relay.Seen.Where(r => r.PointName == point).ToList();
                Assert.True(calls.Count > 0, point + " was never asked");
                var objectsPerCall = calls.Select(r => r.Questions.Select(q => RefOf(q.Name)).Distinct(StringComparer.Ordinal).ToList()).ToList();
                report.Add(point + ": " + string.Join(",", objectsPerCall.Select(o => o.Count)));
                // the bound holds for every request
                Assert.All(objectsPerCall, o => Assert.True(o.Count <= bound, point + " asked " + o.Count + " objects in one request, over its bound of " + bound));
                // every object asked exactly once (nothing lost to the split, nothing asked twice)
                var all = objectsPerCall.SelectMany(o => o).ToList();
                Assert.True(all.Count == all.Distinct(StringComparer.Ordinal).Count(), point + " asked an object twice");
                // requests are full: no more requests than the bound needs, plus one for a cut at a table edge or Jev's context
                var fewest = (int)Math.Ceiling(all.Count / (double)bound);
                Assert.True(calls.Count <= fewest + 1, point + " used " + calls.Count + " requests where " + fewest + " hold " + all.Count + " objects (" + report.Last() + ")");
                // small tables share a request
                if (all.Select(TableOf).Distinct(StringComparer.Ordinal).Count() > 1)
                    Assert.True(objectsPerCall.Any(o => o.Select(TableOf).Distinct(StringComparer.Ordinal).Count() > 1), point + " never put two tables in one request");
                // a table with more objects than the bound is split over requests
                var big = all.GroupBy(TableOf).Where(g => g.Count() > bound).Select(g => g.Key).ToList();
                foreach (var t in big)
                    Assert.True(objectsPerCall.Count(o => o.Any(r => TableOf(r) == t)) >= 2, point + ": " + t + " has more than " + bound + " objects but went in one request");
                if (point is "column.summarize" or "measure.folder_and_format")
                    Assert.True(big.Contains("Activity Event"), point + ": the grown table was not over the bound, so the split was not exercised (" + report.Last() + ")");
            }
            Assert.All(relay.Seen, r => Assert.True(Encoding.UTF8.GetByteCount(JevDecisionOracle.BuildBody(r, DecisionDigest.CanonicalSnapshotJson(r.Snapshot))) <= JevTableBatch.MaxBodyBytes));
        }
    }
}
