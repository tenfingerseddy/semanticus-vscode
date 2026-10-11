// Review-fix round 5 of lane m11/plan-rows (29 Sep): the failing tests for the verifier's round-4 items
// (lane-reports/v-plan-rows-r4.md), under the coordinator's round-5 rulings. Each one was red before its fix.
//
//   L11  a plan with many rows: every default read (the first read, a row's detail page, a history page, a page of rows, a
//        words page) is at most 40,000 characters of the wire JSON, on ANY model, fresh and after Run the plan. A short row
//        names what it waits for by id, and past a few gives a count and the first few. A read never empties the page it was
//        asked for: item = history always returns history entries. Every row, every history entry and every line of one is
//        reachable by paging.
//   L-f  the agent door's other plan results (apply_plan_items, model_plan_action, skip, unskip) answer short: the sentence
//        first, what was left out and why (never cut), the touched rows' new state, the plan's digest and a pointer. The
//        pipe still carries the whole plan for the Studio.
//   L10  a long text of any kind on a words page (description, names, message) is cut, said plainly, with the read that
//        returns it.
//   L12  the Studio's answer does not point at a list it does not show.
//   L13  a history entry cut at 100 lines says which read returns the rest, and that read returns it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanRowsRound5Tests
    {
        private const int Limit = 40_000;

        private static readonly JsonSerializerOptions Wire = ModelContextProtocol.McpJsonUtilities.DefaultOptions;

        // What the MCP door sends is the tool's return value serialized with the SDK's own options (escapes counted).
        private static int WireChars(object o) => JsonSerializer.Serialize(o, Wire).Length;

        private static JsonDocument Doc(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o, Wire));

        // ---- L11 and L13, on a plan shaped like a hub model at 170 tables (fast: no engine) ------------------------------

        private static ModelPlanView ManyRows(int rows, int entries, int linesInEntry)
        {
            string Id(int i) => "starter/table:T_" + i.ToString("000");
            return new ModelPlanView
            {
                ModelName = "Hub",
                ReadyDigest = "digest-1",
                Items = Enumerable.Range(0, rows).Select(i => new ModelPlanItem
                {
                    Id = Id(i), Title = "Add a starter measure to T_" + i.ToString("000"), Sub = "Waits for: Add a starter measure to T_000",
                    Why = new[] { new string('w', 300), new string('y', 200) }, StateWord = "waiting", Doer = "semanticus", Touches = "1 table",
                    WaitsFor = Enumerable.Range(0, i).Select(j => new PlanWaitRef { Id = Id(j), Title = "Add a starter measure to T_" + j.ToString("000") }).ToArray(),
                    Diff = new[] { "+ measure T_" + i.ToString("000") + "[Rows] = COUNTROWS ( 'T_" + i.ToString("000") + "' )" },
                }).ToArray(),
                Tables = Enumerable.Range(0, rows).Select(t => new ModelPlanTable { Name = "T_" + t.ToString("000"), Kind = "fact", Columns = 11, Measures = 2 }).ToArray(),
                History = Enumerable.Range(0, entries).Select(h => new ModelPlanHistoryEntry
                {
                    Kind = "approved", Text = "Approved " + h, Sentence = "Approved " + h, Title = "Run the plan", Revision = h + 1,
                    Detail = Enumerable.Range(0, linesInEntry).Select(n => "change " + n + " of entry " + h + " on 'T_000'[C] with a + sign").ToArray(),
                    Appeared = Enumerable.Range(0, linesInEntry / 2).Select(n => "appeared " + n).ToArray(),
                }).ToArray(),
            };
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void A_plan_of_170_rows_reads_at_40000_or_under_whatever_is_asked_and_never_empties_the_page_asked_for()
        {
            var view = ManyRows(170, 12, 250);
            var asks = new (string Item, int Offset)[]
            {
                (null, 0), ("rows", 0), ("rows", 60), ("rows", 160), ("starter/table:T_150", 0), ("starter/table:T_150", 40),
                ("history", 0), ("history", 6), ("history/3", 0), ("history/3", 200),
            };
            foreach (var (item, offset) in asks)
            {
                var read = PlanReadShape.For(view, item, offset, 0);
                Assert.True(WireChars(read) <= Limit, "item " + (item ?? "none") + " at " + offset + " reads " + WireChars(read) + " characters");
            }
            // History is asked for its entries, so rows give way, never the entries.
            Assert.NotEmpty(PlanReadShape.For(view, "history", 0, 0).History);
            Assert.NotEmpty(PlanReadShape.For(view, "history", 0, 0).DetailNote);
            // A row asked for is always in its own read, with its detail.
            var row = PlanReadShape.For(view, "starter/table:T_150", 0, 0);
            Assert.Contains(row.Items, i => i.Id == "starter/table:T_150");
            Assert.True(row.Detail.Single(d => d.ItemId == "starter/table:T_150").Shown);
        }

        [Fact]
        public void A_single_line_longer_than_the_page_is_cut_and_says_how_long_it_was_and_the_plan_itself_is_not_touched()
        {
            var view = ManyRows(3, 1, 3);
            var huge = "~ measure Sales[Big]: " + new string('x', 60_000);
            view.Items[1].Diff = new[] { huge };
            view.Items[1].Tables = new[] { new ModelPlanItemTable { Name = "Sales", Count = 1, Diff = new[] { huge } } };
            var read = PlanReadShape.For(view, view.Items[1].Id, 0, 0);
            Assert.True(WireChars(read) <= Limit, "the read is " + WireChars(read) + " characters");
            var row = read.Items.Single(i => i.Id == view.Items[1].Id);
            Assert.Contains("[cut here, " + huge.Length + " characters in all]", row.Diff.Single());
            Assert.Contains("is cut to fit", read.DetailNote);
            Assert.Equal(huge, view.Items[1].Diff.Single());        // the plan the Studio reads is as it was
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void A_short_row_names_what_it_waits_for_by_id_and_a_count_and_its_own_read_lists_every_one_with_its_title()
        {
            var view = ManyRows(170, 2, 10);
            var first = PlanReadShape.For(view, "rows", 150, 5);
            var short150 = first.Items.FirstOrDefault(i => i.Id == "starter/table:T_150");
            Assert.NotNull(short150);
            Assert.True(short150.WaitsFor.Length <= 5, "a short row lists " + short150.WaitsFor.Length + " rows it waits for");
            Assert.All(short150.WaitsFor, w => Assert.Null(w.Title));
            using (var d = Doc(short150))
                Assert.Equal(150, d.RootElement.GetProperty("waitsForCount").GetInt32());

            var ids = new List<string>();
            var titled = true;
            int? offset = 0;
            while (offset != null)
            {
                var page = PlanReadShape.For(view, "starter/table:T_150", offset.Value, 0);
                Assert.True(WireChars(page) <= Limit);
                var r = page.Items.Single(i => i.Id == "starter/table:T_150");
                ids.AddRange(r.WaitsFor.Select(w => w.Id));
                titled &= r.WaitsFor.All(w => !string.IsNullOrEmpty(w.Title));
                offset = page.Detail.Single(d => d.ItemId == "starter/table:T_150").Next;
            }
            Assert.True(titled, "the row's own read names a row it waits for without its title");
            Assert.Equal(view.Items[150].WaitsFor.Select(w => w.Id), ids);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void Every_row_and_every_history_entry_and_every_line_of_a_long_one_is_reachable_by_paging()
        {
            var view = ManyRows(170, 12, 250);

            // The rows, a page at a time.
            var seen = new List<string>();
            var offset = 0;
            for (var pages = 0; pages < 300 && seen.Count < 170; pages++)
            {
                var page = PlanReadShape.For(view, "rows", offset, 0);
                Assert.True(page.Items.Length >= 1, "a rows read at " + offset + " carries no row");
                Assert.True(WireChars(page) <= Limit);
                seen.AddRange(page.Items.Select(i => i.Id));
                offset += page.Items.Length;
            }
            Assert.Equal(view.Items.Select(i => i.Id), seen);

            // The history, a page at a time, and each entry's long lists through item = history/N.
            var entries = new List<ModelPlanHistoryEntry>();
            var at = 0;
            for (var pages = 0; pages < 100 && entries.Count < 12; pages++)
            {
                var page = PlanReadShape.For(view, "history", at, 0);
                Assert.NotEmpty(page.History);
                Assert.True(WireChars(page) <= Limit);
                entries.AddRange(page.History);
                at += page.History.Length;
            }
            Assert.Equal(12, entries.Count);
            var first = entries[0];
            var marker = first.Detail.Last();
            Assert.StartsWith("... and ", marker);
            Assert.Contains("history/1", marker);

            var lines = new List<string>();
            int? from = 0;
            while (from != null)
            {
                var page = PlanReadShape.For(view, "history/1", from.Value, 0);
                Assert.True(WireChars(page) <= Limit);
                lines.AddRange(page.History.Single().Detail);
                from = page.DetailNote.Contains("offset = ") ? int.Parse(System.Text.RegularExpressions.Regex.Match(page.DetailNote, @"offset = (\d+)").Groups[1].Value) : (int?)null;
            }
            Assert.Equal(view.History[0].Detail, lines);
            Assert.Contains("none is number 99", PlanReadShape.For(view, "history/99", 0, 0).DetailNote);
        }

        // ---- L11, on the verifier's hub model, fresh and after Run the plan ---------------------------------------------

        // A hub table and N tables that each hang off it: 10 columns and 2 measures each (the verifier's shape).
        private static string HubModelBim(int tables)
        {
            var types = new[] { "int64", "string", "decimal", "dateTime", "int64" };
            var list = new List<object>();
            var rels = new List<object>();
            for (var t = 0; t < tables; t++)
            {
                var name = $"T_{t:000}";
                var cols = Enumerable.Range(0, 10).Select(c =>
                {
                    var cn = $"{name}_C{c:00}_{(c % 3 == 0 ? "KEY" : c % 3 == 1 ? "AMT" : "CNT")}";
                    return (object)new Dictionary<string, object> { ["name"] = cn, ["dataType"] = types[c % 5], ["sourceColumn"] = cn };
                }).ToList();
                if (t > 0)
                {
                    cols.Add(new Dictionary<string, object> { ["name"] = $"{name}_T_000_KEY", ["dataType"] = "int64", ["sourceColumn"] = $"{name}_T_000_KEY" });
                    rels.Add(new Dictionary<string, object>
                    {
                        ["name"] = "r" + t, ["fromTable"] = name, ["fromColumn"] = $"{name}_T_000_KEY", ["toTable"] = "T_000", ["toColumn"] = "T_000_C00_KEY",
                    });
                }
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["columns"] = cols,
                    ["measures"] = new object[]
                    {
                        new Dictionary<string, object> { ["name"] = name + "_M0", ["expression"] = $"SUM ( '{name}'[{name}_C02_CNT] )" },
                        new Dictionary<string, object> { ["name"] = name + "_M1", ["expression"] = $"COUNTROWS ( '{name}' )" },
                    },
                    ["partitions"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = name, ["mode"] = "import",
                            ["source"] = new Dictionary<string, object> { ["type"] = "m", ["expression"] = "let\n    Source = #table({}, {})\nin\n    Source" },
                        },
                    },
                });
            }
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = "Hub" + tables, ["compatibilityLevel"] = 1604,
                ["model"] = new Dictionary<string, object> { ["culture"] = "en-US", ["tables"] = list, ["relationships"] = rels },
            });
        }

        private static async Task<List<string>> EveryDefaultReadAsync(DecisionFixture f)
        {
            var problems = new List<string>();
            void Check(string what, int chars) { if (chars > Limit) problems.Add(what + " is " + chars + " characters"); }
            var first = await McpTools.GetModelPlan(f.Engine);
            Check("the first read (" + first.Items.Length + " rows)", WireChars(first));
            var whole = await f.Engine.GetModelPlanAsync();
            // The rows, a page at a time, reach every row.
            var seen = 0;
            var offset = 0;
            for (var pages = 0; pages < 200 && seen < whole.Items.Length; pages++)
            {
                var page = await McpTools.GetModelPlan(f.Engine, "rows", offset, 0);
                Check("the rows page at " + offset, WireChars(page));
                if (page.Items.Length == 0) { problems.Add("the rows page at " + offset + " carries no row"); break; }
                seen += page.Items.Length;
                offset += page.Items.Length;
            }
            if (seen != whole.Items.Length) problems.Add("paging the rows reached " + seen + " of " + whole.Items.Length);
            foreach (var row in whole.Items)
            {
                var lines = row.Diff.Length + row.Objects.Length + row.WaitsFor.Length;
                if (lines > 0) Check("the first detail page of " + row.Id, WireChars(await McpTools.GetModelPlan(f.Engine, row.Id, 0, 0)));
            }
            var history = await McpTools.GetModelPlan(f.Engine, "history", 0, 0);
            Check("the first history page", WireChars(history));
            if (whole.History.Length > 0 && (history.History?.Length ?? 0) == 0)
                problems.Add("the plan has " + whole.History.Length + " history entries and item = history carries none (" + history.DetailNote + ")");
            var words = await McpTools.GetWordsQueue(f.Engine, 0, 0);
            Check("the first words page", WireChars(words));
            return problems;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(8)]
        [InlineData(22)]
        public async Task On_a_hub_model_every_default_plan_read_is_at_40000_or_under_fresh_and_after_Run_the_plan(int tables)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var path = System.IO.Path.Combine(f.Workspace, "Hub" + tables + ".bim");
            await System.IO.File.WriteAllTextAsync(path, HubModelBim(tables));
            await f.Engine.OpenAsync(path);
            f.UseNull();

            var fresh = await EveryDefaultReadAsync(f);
            Assert.True(fresh.Count == 0, "fresh, at " + tables + " tables: " + string.Join("; ", fresh));

            for (var press = 0; press < 2; press++)
            {
                var plan = await f.Engine.GetModelPlanAsync();
                // One press runs every wave (round 1), so the second press has nothing left for Semanticus to run.
                if (press > 0 && !plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready)) break;
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                Assert.True(ran.Performed, ran.Sentence);
                var after = await EveryDefaultReadAsync(f);
                Assert.True(after.Count == 0, "after " + (press + 1) + " press(es), at " + tables + " tables: " + string.Join("; ", after));
            }
        }

        // ---- L-f: the agent door's other plan results -----------------------------------------------------------------

        private static async Task<DecisionFixture> TwelveTablesAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("R5Twelve", 1604);
            for (var t = 0; t < 12; t++)
            {
                var table = "TBL_" + (char)('A' + t);
                await f.Engine.CreateTableAsync(table, "human");
                var decimals = new List<string>();
                for (var c = 0; c < 25; c++)
                {
                    var type = c % 5 == 0 ? "Int64" : c % 5 == 1 ? "String" : c % 5 == 2 ? "Decimal" : c % 5 == 3 ? "DateTime" : "Int64";
                    var name = $"{table}_COL_{c:00}_{(c % 3 == 0 ? "KEY" : c % 3 == 1 ? "AMT" : "CNT")}";
                    await f.Engine.CreateColumnAsync("table:" + table, name, type, null, "human");
                    if (type == "Decimal") decimals.Add(name);
                }
                for (var m = 0; m < 4; m++)
                    await f.Engine.CreateMeasureAsync("table:" + table, $"{table}_MEAS_{m}", $"SUM ( '{table}'[{decimals[m % decimals.Count]}] )", "human");
            }
            await f.SaveAsAsync("R5Twelve.bim");
            f.UseNull();
            return f;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Run_the_plan_answers_short_on_the_agent_door_and_whole_on_the_pipe()
        {
            using var agent = await TwelveTablesAsync();
            using var pipe = await TwelveTablesAsync();
            var planA = await agent.Engine.GetModelPlanAsync();
            var planP = await pipe.Engine.GetModelPlanAsync();
            Assert.True(WireChars(planA) > Limit, "the fixture's plan is only " + WireChars(planA) + " characters, so it proves nothing");

            var ran = await McpTools.ApplyPlanItems(agent.Engine, null, planA.ReadyDigest);          // the agent door
            var text = JsonSerializer.Serialize(ran, Wire);
            Assert.True(text.Length <= Limit, "Run the plan answers " + text.Length + " characters on the agent door");
            Assert.StartsWith("{\"sentence\":", text);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("performed").GetBoolean(), root.GetProperty("sentence").GetString());
            Assert.StartsWith("Applied ", root.GetProperty("sentence").GetString(), StringComparison.Ordinal);
            Assert.False(root.TryGetProperty("plan", out _), "the answer still carries the whole plan");
            Assert.Contains("get_model_plan", root.GetProperty("more").GetString());
            // The touched rows' new state, and the plan's digest now.
            var rows = root.GetProperty("rows").EnumerateArray().ToArray();
            Assert.NotEmpty(rows);
            var now = await agent.Engine.GetModelPlanAsync();
            Assert.Equal(now.ReadyDigest, root.GetProperty("readyDigest").GetString());
            foreach (var r in rows)
            {
                var id = r.GetProperty("id").GetString();
                Assert.Equal(now.Items.Single(i => i.Id == id).StateWord, r.GetProperty("stateWord").GetString());
            }

            // The pipe (the Studio's path) still carries the whole plan beside the sentence.
            var viaPipe = await pipe.Engine.ApplyPlanItemsAsync(null, "human", planP.ReadyDigest);
            Assert.True(viaPipe.Performed, viaPipe.Sentence);
            Assert.NotNull(viaPipe.Plan);
            Assert.Equal((await pipe.Engine.GetModelPlanAsync()).Items.Length, viaPipe.Plan.Items.Length);
            Assert.Equal(viaPipe.Sentence, root.GetProperty("sentence").GetString());
            Assert.Equal(viaPipe.Applied, root.GetProperty("applied").GetInt32());
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_other_plan_calls_answer_short_too_and_a_skip_can_be_undone()
        {
            using var f = await TwelveTablesAsync();
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.True(WireChars(plan) > Limit);
            var target = plan.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal) || i.Id == "bpafix/model").Id;

            var proposed = await McpTools.ModelPlanAction(f.Engine, target, "propose");
            Assert.True(WireChars(proposed) <= Limit, "propose answers " + WireChars(proposed) + " characters");
            using (var d = Doc(proposed))
            {
                Assert.False(d.RootElement.TryGetProperty("plan", out _));
                var row = d.RootElement.GetProperty("rows").EnumerateArray().Single();
                Assert.Equal(target, row.GetProperty("id").GetString());
                Assert.False(string.IsNullOrEmpty(row.GetProperty("changeDigest").GetString()), "the row's new digest is not in the answer");
            }

            var skipped = await McpTools.SkipModelPlanItem(f.Engine, target, "Not now.");
            Assert.True(WireChars(skipped) <= Limit, "skip answers " + WireChars(skipped) + " characters");
            using (var d = Doc(skipped))
            {
                Assert.StartsWith("{\"sentence\":", JsonSerializer.Serialize(skipped, Wire));
                Assert.Equal("set_aside", d.RootElement.GetProperty("rows").EnumerateArray().Single().GetProperty("stateWord").GetString());
            }
            var back = await McpTools.UnskipModelPlanItem(f.Engine, target);
            Assert.True(WireChars(back) <= Limit, "unskip answers " + WireChars(back) + " characters");
            using (var d = Doc(back))
                Assert.NotEqual("set_aside", d.RootElement.GetProperty("rows").EnumerateArray().Single().GetProperty("stateWord").GetString());
        }

        // One fact table and measures a person has partly formatted, as in round 4: Run the plan leaves four fixes out.
        private static async Task<DecisionFixture> FormattedModelAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Formats", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Qty", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Rate", "Double", null, "human");
            // Three more Double columns (join, group G1): since the format fill rule (m11/feedback-rules) a format it cannot
            // prove is advice and never a fix, so propose_plan no longer seeds the formats below and the run does not leave
            // them out. What it still leaves out is each data type change, so the fixture carries four of them.
            await f.Engine.CreateColumnAsync("table:Sales", "Rate Two", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Rate Three", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Rate Four", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( SUM ( Sales[Amount] ), SUM ( Sales[Amount] ) + 1 )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Rows", "COUNTROWS ( Sales )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Money", "SUM ( Sales[Amount] )", "human");
            await f.Engine.SetMeasureFormatAsync("measure:Sales/Money", "#,##0.00", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin Pct", "DIVIDE ( SUM ( Sales[Amount] ), 3 )", "human");
            await f.Engine.SetMeasureFormatAsync("measure:Sales/Margin Pct", "0.0%", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_answer_that_names_what_was_left_out_lists_every_one_on_the_agent_door_and_never_points_the_studio_at_a_list_it_does_not_show()
        {
            using var agent = await FormattedModelAsync("R5AnswerAgent.bim");
            using var studio = await FormattedModelAsync("R5AnswerStudio.bim");
            await agent.Engine.ProposePlanAsync(null, true, 40, "agent");
            await studio.Engine.ProposePlanAsync(null, true, 40, "agent");
            var planA = await agent.Engine.GetModelPlanAsync();
            var planS = await studio.Engine.GetModelPlanAsync();

            var ran = await McpTools.ApplyPlanItems(agent.Engine, null, planA.ReadyDigest);
            using var doc = Doc(ran);
            var sentence = doc.RootElement.GetProperty("sentence").GetString();
            var skipped = doc.RootElement.GetProperty("skipped").EnumerateArray().ToArray();
            var viaPipe = await studio.Engine.ApplyPlanItemsAsync(null, "human", planS.ReadyDigest);

            // Every fix left out is listed on the agent door, the same ones the pipe reports.
            Assert.True(skipped.Length >= 4, "the agent door lists " + skipped.Length + " left out");
            Assert.Equal(viaPipe.Skipped.Length, skipped.Length);
            Assert.Contains(skipped, s => (s.GetProperty("reason").GetString() ?? "").Contains("data type", StringComparison.OrdinalIgnoreCase));

            // The Studio shows the sentence and no field, so it must not send the person to one (L12).
            Assert.True(viaPipe.Skipped.Length > 3, "the fixture must leave out more than the three the sentence names");
            Assert.DoesNotContain("skipped", viaPipe.Sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("skipped", sentence, StringComparison.Ordinal);
            Assert.Contains("Model quality lists them", viaPipe.Sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("—", viaPipe.Sentence);
        }

        // A run that leaves out hundreds of fixes: the answer names every kind, lists as many as fit and says how many it does not.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_run_that_leaves_out_hundreds_of_fixes_answers_at_40000_or_under_and_says_how_many_are_not_listed()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            // 400 data type changes (join, group G1): the 400 ratio formats this used are advice since the format fill rule
            // (m11/feedback-rules) and are no longer fixes a run leaves out; a data type change still is, every one.
            var measures = new[] { new Dictionary<string, object> { ["name"] = "Revenue", ["expression"] = "SUM ( Sales[Amount] )" } };
            var columns = new List<object> { new Dictionary<string, object> { ["name"] = "Amount", ["dataType"] = "decimal", ["sourceColumn"] = "Amount" } };
            columns.AddRange(Enumerable.Range(0, 400).Select(k => (object)new Dictionary<string, object>
                { ["name"] = "Rate " + k.ToString("000") + " " + new string('x', 60), ["dataType"] = "double", ["sourceColumn"] = "Rate " + k.ToString("000") }));
            var bim = new Dictionary<string, object>
            {
                ["name"] = "Ratios", ["compatibilityLevel"] = 1604,
                ["model"] = new Dictionary<string, object>
                {
                    ["culture"] = "en-US",
                    ["tables"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = "Sales",
                            ["columns"] = columns,
                            ["measures"] = measures,
                            ["partitions"] = new object[] { new Dictionary<string, object> { ["name"] = "Sales", ["mode"] = "import",
                                ["source"] = new Dictionary<string, object> { ["type"] = "m", ["expression"] = "let\n    Source = #table({}, {})\nin\n    Source" } } },
                        },
                    },
                },
            };
            var path = System.IO.Path.Combine(f.Workspace, "Ratios.bim");
            await System.IO.File.WriteAllTextAsync(path, JsonSerializer.Serialize(bim));
            await f.Engine.OpenAsync(path);
            f.UseNull();
            await f.Engine.ProposePlanAsync(null, true, 40, "agent");
            var plan = await f.Engine.GetModelPlanAsync();
            var ran = await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest);
            var text = JsonSerializer.Serialize(ran, Wire);
            Assert.True(text.Length <= Limit, "the answer is " + text.Length + " characters");
            using var doc = JsonDocument.Parse(text);
            var sentence = doc.RootElement.GetProperty("sentence").GetString();
            var listed = doc.RootElement.GetProperty("skipped").GetArrayLength();
            Assert.True(listed >= 1 && listed < 400, "the answer lists " + listed + " left out");
            Assert.Contains("more", doc.RootElement.GetProperty("skippedNote").GetString(), StringComparison.Ordinal);
            Assert.Contains("400", doc.RootElement.GetProperty("skippedNote").GetString(), StringComparison.Ordinal);
            Assert.Contains("best-practice fixes were left out", sentence, StringComparison.Ordinal);
        }

        // ---- L10: any long text on a words page ---------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_words_page_cuts_any_long_text_says_so_and_get_grounding_returns_the_whole_value()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("LongTexts", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Revenue", "SUM ( Sales[Amount] )", "human");
            var text = string.Concat(Enumerable.Repeat("Revenue recognised in the period after credits and before tax. ", 800)).Substring(0, 48_000);
            await f.Engine.SetDescriptionAsync("measure:Sales/Revenue", text, "human");
            // Every kind of object with a long description of its own, and a formula of 30,000 characters: each object's grounding
            // is cut whatever makes it long.
            await f.Engine.SetDescriptionAsync("table:Sales", text, "human");
            await f.Engine.SetDescriptionAsync("column:Sales/Amount", text, "human");
            for (var k = 0; k < 6; k++)
            {
                await f.Engine.CreateMeasureAsync("table:Sales", "Long" + k, string.Join(" + ", Enumerable.Repeat("SUM ( Sales[Amount] )", 1_500)), "human");
                await f.Engine.SetDescriptionAsync("measure:Sales/Long" + k, text, "human");
            }

            // Names stop at 512 characters, and a grounding names up to 50 siblings: the names are cut too.
            for (var k = 0; k < 30; k++)
                await f.Engine.CreateMeasureAsync("table:Sales", "Sibling" + k.ToString("00") + new string((char)('a' + k % 20), 500), "COUNTROWS ( Sales )", "human");

            var seen = 0;
            var over = new List<string>();
            var cutMarked = false;
            var siblingsCut = false;
            int? offset = 0;
            var total = 0;
            for (var pages = 0; offset != null && pages < 200; pages++)
            {
                var page = await McpTools.GetWordsQueue(f.Engine, 0, offset.Value);
                if (WireChars(page) > Limit) over.Add("offset " + offset + " " + WireChars(page));
                seen += page.Items.Length;
                total = page.Total;
                cutMarked |= page.Grounding.Values.Any(g => (g.ExistingDescription ?? "").Contains("get_grounding has the whole description]"));
                siblingsCut |= page.Grounding.Values.Any(g => g.SiblingNames.Any(n => n.Contains("get_grounding has the whole name]")));
                offset = page.Next;
            }
            Assert.True(over.Count == 0, "words pages over 40,000: " + string.Join(", ", over));
            Assert.Equal(total, seen);
            Assert.True(cutMarked, "the long description was not cut and marked");
            Assert.True(siblingsCut, "the long sibling names were not cut and marked");
            var whole = await f.Engine.GetGroundingAsync("measure:Sales/Revenue");
            Assert.Equal(text, whole.ExistingDescription);
        }
    }
}
