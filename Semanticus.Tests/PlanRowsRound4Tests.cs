// Review-fix round 4 of lane m11/plan-rows (29 Sep): the failing tests for the verifier's third round
// (lane-reports/v-plan-rows-r3.md), under the coordinator's rulings. Each one was red before its fix.
//
//   V9  NO LINE SKIPS THE CHECK BECAUSE OF WHO PROPOSED IT. Every best-practice fix a Model Plan batch writes (Run the
//       plan, apply_plan_items, approve) goes through the row's own refusal AT WRITE TIME, whether the row worked the
//       line out or the change plan seeded it. A data type change is never made by Run the plan.
//   V8/L9  a default read stays at or under 40,000 characters of the JSON as it goes on the wire, on any model and after
//       Run the plan has been pressed several times: the plan's history is a count and the last few, the rest paged.
//   L-g  the best-practice format findings that remain are listed in the words queue, after the words.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanRowsRound4Tests : IClassFixture<ModelTemplates>
    {
        // Lean suite (8 Oct): the models below are built once per class (ModelTemplates) and each test opens its own copy.
        private readonly ModelTemplates _templates;
        public PlanRowsRound4Tests(ModelTemplates templates) => _templates = templates;

        // What the MCP door sends is the tool's return value serialized with the SDK's own options (escapes counted).
        private static int WireChars(object o) => JsonSerializer.Serialize(o, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length;

        private const int DefaultReadLimit = 40_000;

        private static async Task<string> PropertyOf(IEngine engine, string objRef, string name) =>
            (await engine.GetObjectPropertiesAsync(objRef)).FirstOrDefault(p => p.Name == name)?.Value?.ToString() ?? "";

        // ---- V9 -----------------------------------------------------------------------------------------------

        // One fact table and measures a person has partly formatted (as in the verifier's V9 model): a ratio (none), a
        // count (none), money with cents (#,##0.00) and a percent (0.0%). No relationship, so no safe-fix row: the
        // best-practice row is ready from the start.
        private async Task<DecisionFixture> FormattedModelAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "formatted", BuildFormattedAsync, file);
            f.UseNull();
            return f;
        }

        private static async Task BuildFormattedAsync(DecisionFixture f)
        {
            await f.Engine.CreateModelAsync("Formats", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Qty", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Rate", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( SUM ( Sales[Amount] ), SUM ( Sales[Amount] ) + 1 )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Rows", "COUNTROWS ( Sales )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Money", "SUM ( Sales[Amount] )", "human");
            await f.Engine.SetMeasureFormatAsync("measure:Sales/Money", "#,##0.00", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin Pct", "DIVIDE ( SUM ( Sales[Amount] ), 3 )", "human");
            await f.Engine.SetMeasureFormatAsync("measure:Sales/Margin Pct", "0.0%", "human");
        }

        private static async Task AssertNothingRefusedWasWrittenAsync(IEngine engine, string what)
        {
            Assert.True(await PropertyOf(engine, "measure:Sales/Money", "FormatString") == "#,##0.00", what + ": Money's format changed");
            Assert.True(await PropertyOf(engine, "measure:Sales/Margin Pct", "FormatString") == "0.0%", what + ": Margin Pct's format changed");
            Assert.True((await PropertyOf(engine, "measure:Sales/Share", "FormatString")).Length == 0, what + ": the ratio Share got a format");
            Assert.True(await PropertyOf(engine, "column:Sales/Rate", "DataType") == "Double", what + ": Sales[Rate] changed its data type");
        }

        [Fact]
        public async Task Run_the_plan_after_propose_plan_names_what_it_left_out_and_still_makes_the_fixes_it_can_prove()
        {
            using var f = await FormattedModelAsync("R4RunNames.bim");
            await f.Engine.ProposePlanAsync(null, true, 40, "agent");
            var plan = await f.Engine.GetModelPlanAsync();
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(ran.Performed, ran.Sentence);
            await AssertNothingRefusedWasWrittenAsync(f.Engine, "Run the plan after propose_plan");
            Assert.True(await PropertyOf(f.Engine, "measure:Sales/Rows", "FormatString") == "#,0",
                "the count's provable format was not made: " + ran.Sentence);
            var left = (ran.Skipped ?? Array.Empty<ApplyPlanSkip>()).Select(s => (s.What ?? "") + " " + s.Reason).ToArray();
            Assert.Contains(left, s => s.Contains("data type", StringComparison.OrdinalIgnoreCase) && s.Contains("Rate", StringComparison.Ordinal));
            // Money already has a format: since m11/feedback-rules the scan withholds that fix itself, so propose_plan never seeds it and
            // there is nothing at the write to leave out. AssertNothingRefusedWasWrittenAsync above proves its format was not touched.
            Assert.DoesNotContain("—", string.Join(" ", left) + ran.Sentence);
        }

        [Fact]
        public async Task Apply_plan_items_with_the_rows_own_digest_never_writes_the_change_plans_refused_fixes()
        {
            using var f = await FormattedModelAsync("R4ApplyItems.bim");
            await f.Engine.ProposePlanAsync(null, true, 40, "agent");
            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "bpafix/model");
            Assert.NotNull(row.ChangeSet?.Digest);
            var ran = await f.Engine.ApplyPlanItemsAsync(new[] { new PlanItemDigest { Id = row.Id, Digest = row.ChangeSet.Digest } }, "agent", null);
            Assert.True(ran.Performed, ran.Sentence);
            await AssertNothingRefusedWasWrittenAsync(f.Engine, "apply_plan_items");
        }

        [Fact]
        public async Task Approving_the_best_practice_row_with_a_refused_fix_in_its_set_changes_nothing_and_says_why()
        {
            using var f = await FormattedModelAsync("R4Approve.bim");
            await f.Engine.ProposePlanAsync(null, true, 40, "agent");
            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "bpafix/model");
            var res = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "agent", row.ChangeSet.Digest);
            Assert.False(res.Performed, "approve wrote a set that holds a fix the row refuses: " + res.Sentence);
            Assert.Contains("nothing was changed", res.Sentence, StringComparison.OrdinalIgnoreCase);
            await AssertNothingRefusedWasWrittenAsync(f.Engine, "approve");
            Assert.True((await PropertyOf(f.Engine, "measure:Sales/Rows", "FormatString")).Length == 0, "approve is all or none, and it wrote a line");
        }

        // ---- V8 and L9: the default read at 40,000 --------------------------------------------------------------

        private static string LongDax(string table, int k, IReadOnlyList<string> cols, int chars)
        {
            var terms = new List<string>();
            var dax = "";
            for (var i = k; dax.Length < chars; i += 7)
            {
                terms.Add($"SUM ( '{table}'[{cols[i % cols.Count]}] )");
                dax = string.Join(" + ", terms);
            }
            return dax;
        }

        private static Task BuildLongDaxAsync(DecisionFixture f, int daxChars)
        {
            var cols = Enumerable.Range(0, 20).Select(c => $"AMT_{c:00}").ToList();
            var measures = Enumerable.Range(0, 30).Select(k => PlanRowsBim.M($"M_{k:00}", LongDax("Fact", k, cols, daxChars))).ToArray();
            return PlanRowsBim.LoadAsync(f, "LongDax" + daxChars, "Build.bim",
                new object[] { PlanRowsBim.Table("Fact", cols.Select(c => c + ":Decimal").ToArray(), measures) });
        }

        // Lean suite: the longest text is the one that mattered (the survivor of the old VerifyPlanRowsRound3SizeTests V8, which asked
        // the same of a 50,000 limit); the two shorter sizes are the same walk and run in the sweep.
        [Theory]
        [InlineData(2600)]
        public Task Every_default_words_page_stays_under_40000_characters_on_the_wire(int daxChars) => WordsPagesStayUnderAsync(daxChars);

        [Theory]
        [Trait(TestCost.Key, TestCost.Sweep)]
        [InlineData(700)]
        [InlineData(1500)]
        public Task Every_default_words_page_stays_under_40000_characters_on_the_wire_at_shorter_texts(int daxChars) => WordsPagesStayUnderAsync(daxChars);

        private async Task WordsPagesStayUnderAsync(int daxChars)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "longdax" + daxChars, x => BuildLongDaxAsync(x, daxChars), "LongDax" + daxChars + ".bim");

            var sizes = new List<int>();
            var seen = 0;
            int? offset = 0;
            int total = 0;
            while (offset != null && sizes.Count < 300)
            {
                var page = await McpTools.GetWordsQueue(f.Engine, 0, offset.Value);
                sizes.Add(WireChars(page));
                seen += page.Items.Length;
                total = page.Total;
                offset = page.Next;
            }
            var over = sizes.Where(s => s > DefaultReadLimit).ToList();
            Assert.True(over.Count == 0, over.Count + " of " + sizes.Count + " default words pages are over 40,000 characters: " + string.Join(", ", over));
            Assert.Equal(total, seen);
        }

        private async Task<DecisionFixture> TwelveTablesAsync(int daxChars)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "twelve" + daxChars, x => BuildTwelveTablesAsync(x, daxChars), "R4Twelve" + daxChars + ".bim");
            f.UseNull();
            return f;
        }

        private static Task BuildTwelveTablesAsync(DecisionFixture f, int daxChars)
        {
            var tables = new List<object>();
            for (var t = 0; t < 8; t++)
            {
                var table = "TBL_" + (char)('A' + t);
                var columns = new List<string>();
                var decimals = new List<string>();
                for (var c = 0; c < 25; c++)
                {
                    var type = c % 5 == 0 ? "Int64" : c % 5 == 1 ? "String" : c % 5 == 2 ? "Decimal" : c % 5 == 3 ? "DateTime" : "Int64";
                    var name = $"{table}_COL_{c:00}_{(c % 3 == 0 ? "KEY" : c % 3 == 1 ? "AMT" : "CNT")}";
                    columns.Add(name + ":" + type);
                    if (type == "Decimal") decimals.Add(name);
                }
                var measures = Enumerable.Range(0, 4).Select(m => PlanRowsBim.M($"{table}_MEAS_{m}", LongDax(table, m, decimals, daxChars))).ToArray();
                tables.Add(PlanRowsBim.Table(table, columns.ToArray(), measures));
            }
            return PlanRowsBim.LoadAsync(f, "Twelve" + daxChars, "Build.bim", tables.ToArray());
        }

        private static async Task<int> PressAsync(IEngine engine, int presses)
        {
            var done = 0;
            for (var press = 0; press < presses; press++)
            {
                var plan = await engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.StateWord == PlanStateWords.Ready && i.Doer == PlanDoers.Semanticus)) break;
                var ran = await engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                if (!ran.Performed) break;
                done++;
            }
            return done;
        }

        // Lean suite: 2000 is the survivor of the old VerifyPlanRowsRound3SizeTests L9; 300 is the same walk on short texts (sweep).
        [Theory]
        [InlineData(2000)]
        public Task After_several_presses_every_default_plan_read_stays_under_40000_characters_and_history_pages(int daxChars) => PressesStayUnderAsync(daxChars);

        [Theory]
        [Trait(TestCost.Key, TestCost.Sweep)]
        [InlineData(300)]
        public Task After_several_presses_every_default_plan_read_stays_under_40000_characters_and_history_pages_at_short_texts(int daxChars) => PressesStayUnderAsync(daxChars);

        private async Task PressesStayUnderAsync(int daxChars)
        {
            using var f = await TwelveTablesAsync(daxChars);
            var pressed = await PressAsync(f.Engine, 5);
            Assert.True(pressed >= 1, "the fixture never ran the plan");

            var first = await McpTools.GetModelPlan(f.Engine);
            var reads = new List<(string What, int Chars)> { ("the first read", WireChars(first)) };
            foreach (var d in first.Detail ?? Array.Empty<PlanRowDetail>())
                if (d.Lines > 0)
                    reads.Add(("the first detail page of " + d.ItemId, WireChars(await McpTools.GetModelPlan(f.Engine, d.ItemId, 0, 0))));

            // The plan's history is a count and the last few on a read; every entry is on a page of the history.
            var whole = await f.Engine.GetModelPlanAsync();
            var entries = whole.History.Length;
            Assert.True(first.History.Length <= 5, "a read carries " + first.History.Length + " history entries; it should carry the last few");
            Assert.Contains(entries.ToString(), first.DetailNote ?? "");
            var got = 0;
            var offset = 0;
            for (var pages = 0; pages < 100 && got < entries; pages++)
            {
                var page = await McpTools.GetModelPlan(f.Engine, "history", offset, 0);
                reads.Add(("history page at " + offset, WireChars(page)));
                Assert.NotEmpty(page.History);
                got += page.History.Length;
                offset += page.History.Length;
            }
            Assert.Equal(entries, got);

            var over = reads.Where(r => r.Chars > DefaultReadLimit).ToList();
            Assert.True(over.Count == 0, "after " + pressed + " presses, " + over.Count + " of " + reads.Count + " default reads are over 40,000 characters: "
                + string.Join("; ", over.Select(r => r.What + " " + r.Chars)));
        }

        // A plan whose short rows and map alone are large (hundreds of tables, a history that ran thousands of lines) still
        // reads at 40,000 or under, whatever is asked, with every row still named.
        [Fact]
        public void A_plan_whose_short_rows_and_map_are_large_still_reads_at_most_40000_characters_whatever_is_asked()
        {
            var view = new ModelPlanView
            {
                ModelName = "Big",
                Items = Enumerable.Range(0, 30).Select(i => new ModelPlanItem
                {
                    Id = "row/" + i, Title = "Row " + i, Why = new[] { new string('x', 300) },
                    Diff = Enumerable.Range(0, 400).Select(n => "line " + n + " of row " + i + " on 'T'[C] + 1").ToArray(),
                }).ToArray(),
                Tables = Enumerable.Range(0, 3000).Select(t => new ModelPlanTable { Name = "Table_" + t, Kind = "fact", Columns = 20, Measures = 4 }).ToArray(),
                History = Enumerable.Range(0, 200).Select(h => new ModelPlanHistoryEntry
                {
                    Kind = "approved", Text = "Approved", Sentence = "Approved", Title = "Fix things",
                    Detail = Enumerable.Range(0, 500).Select(n => "change " + n + " on 'Table'[Column] with a + sign").ToArray(),
                }).ToArray(),
            };
            foreach (var (item, offset) in new (string, int)[] { (null, 0), ("row/3", 0), ("row/3", 40), ("history", 0), ("history", 20) })
            {
                var read = PlanReadShape.For(view, item, offset, 0);
                Assert.True(WireChars(read) <= DefaultReadLimit, "item " + (item ?? "none") + " at " + offset + " reads " + WireChars(read) + " characters");
                Assert.Equal(30, read.Items.Length);
                Assert.True(read.History.Length <= 40, "a read carries " + read.History.Length + " history entries");
            }
        }

        // ---- L-g: the format findings the assistant is left with ------------------------------------------------

        [Fact]
        public async Task The_words_queue_lists_the_best_practice_format_findings_that_remain_after_the_words()
        {
            using var f = await FormattedModelAsync("R4Queue.bim");
            var q = await McpTools.GetWordsQueue(f.Engine, 0, 0);
            var bpa = q.Items.Where(i => i.RuleId is "INTEGER_FORMATTING" or "PERCENTAGE_FORMATTING").ToArray();
            Assert.Contains(bpa, i => i.ObjectRef == "measure:Sales/Money");
            Assert.Contains(bpa, i => i.ObjectRef == "measure:Sales/Margin Pct");
            Assert.All(bpa, i =>
            {
                Assert.Equal("format", i.Kind);
                Assert.Equal("set_measure_format", i.Tool);
                Assert.False(string.IsNullOrWhiteSpace(i.Message));
                Assert.DoesNotContain("—", i.Message);
            });
            Assert.Contains("#,0", bpa.Single(i => i.ObjectRef == "measure:Sales/Money").Message);
            // A measure with no format at all is asked for once, by the readiness rule, not twice.
            Assert.Single(q.Items.Where(i => i.ObjectRef == "measure:Sales/Share" && i.Tool == "set_measure_format"));
            // They come after every finding that carries a grade lift, so the words still lead.
            var firstBpa = Array.FindIndex(q.Items, i => i.RuleId is "INTEGER_FORMATTING" or "PERCENTAGE_FORMATTING");
            Assert.All(q.Items.Skip(firstBpa), i => Assert.True(i.RuleId is "INTEGER_FORMATTING" or "PERCENTAGE_FORMATTING", i.RuleId + " comes after a best-practice format item"));
            Assert.Equal(q.Total, q.Kinds.Values.Sum());
        }
    }
}
