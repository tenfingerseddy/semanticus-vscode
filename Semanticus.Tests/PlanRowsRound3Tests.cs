// Review-fix round 3 of lane m11/plan-rows (29 Sep): the failing tests for the verifier's second round
// (lane-reports/v-plan-rows-r2.md). Each one was red before its fix.
//
//   V7  the best-practice row writes a format only when FormatFill.MayFill proves it, whichever property the rule's
//       fix names (FormatString, FormatStringExpression, TargetFormatString, a calculation item's expression).
//   V8  a page of get_words_queue fits an assistant's read on any model: it is sized in characters, and an object's
//       grounding (its DAX and facts) is sent once per page, not once per item.
//   L9  get_model_plan's first read fits the same limit on a large model: every row with its state and counts, and a
//       row's detail paged on request.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanRowsRound3Tests
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        private static int Size(object o) => JsonSerializer.Serialize(o, Web).Length;

        // The limit the brief names: a default read stays under 60,000 characters (about 15,000 tokens) on any model.
        private const int ReadLimit = 60_000;

        // ---- V7 -----------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("formatstringexpression")]
        [InlineData("targetformatstring")]
        [InlineData("TargetFormatString")]
        [InlineData("FormatStringExpression")]
        public async Task A_fix_that_writes_any_format_property_but_a_proven_format_string_is_refused_with_a_plain_note(string target)
        {
            using var f = await PlanRowsBim.PlanModelAsync("R3RefuseFormat.bim");
            var count = await f.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            var line = new ChangeItem { ObjectRef = count, Kind = "bpa_fix", Target = target, Before = "(none)", After = "#,0" };
            var why = await f.Engine.BpaLineRefusalForTest(line, count);
            Assert.False(string.IsNullOrWhiteSpace(why), "a " + target + " write was let through as a plain property write");
            Assert.DoesNotContain("—", why);
        }

        // The default tier keeps the row that writes the format string EXPRESSION (the dynamic format a person did not ask for); the four spellings run in the sweep.
        [Fact]
        public Task A_fix_that_writes_any_format_property_but_a_proven_format_string_is_refused_with_a_plain_note_sample() =>
            A_fix_that_writes_any_format_property_but_a_proven_format_string_is_refused_with_a_plain_note("FormatStringExpression");

        [Fact]
        public async Task A_fix_that_writes_a_proven_format_string_is_still_let_through()
        {
            using var f = await PlanRowsBim.PlanModelAsync("R3AllowFormat.bim");
            var count = await f.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            var line = new ChangeItem { ObjectRef = count, Kind = "bpa_fix", Target = "formatstring", Before = "(none)", After = "#,0" };
            Assert.Null(await f.Engine.BpaLineRefusalForTest(line, count));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_calculation_items_format_string_expression_is_refused()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("R3CalcItemFormat.bim");
            var group = await f.Engine.CreateCalculationGroupAsync("Time", "human");
            var item = await f.Engine.CreateCalculationItemAsync(group, "YTD", "SELECTEDMEASURE ()", "human");
            var line = new ChangeItem { ObjectRef = item, Kind = "bpa_fix", Target = "formatstringexpression", Before = "(none)", After = "\"0\"" };
            Assert.False(string.IsNullOrWhiteSpace(await f.Engine.BpaLineRefusalForTest(line, item)));
        }

        // ---- V8 -----------------------------------------------------------------------------------------------

        private static async Task<DecisionFixture> LongDaxModelAsync(int daxChars, int measures = 60)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("LongDax" + daxChars, 1604);
            await f.Engine.CreateTableAsync("Fact", "human");
            for (var c = 0; c < 20; c++)
                await f.Engine.CreateColumnAsync("table:Fact", $"AMT_{c:00}", "Decimal", $"AMT_{c:00}", "human");
            for (var k = 0; k < measures; k++)
            {
                var terms = new List<string>();
                var dax = "";
                for (var i = k; dax.Length < daxChars; i += 7)
                {
                    terms.Add($"SUM ( 'Fact'[AMT_{i % 20:00}] )");
                    dax = string.Join(" + ", terms);
                }
                await f.Engine.CreateMeasureAsync("table:Fact", $"M_{k:00}", dax, "human");
            }
            return f;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(1000)]
        [InlineData(2000)]
        [InlineData(3000)]
        public async Task A_default_words_page_stays_under_the_reading_limit_and_sends_each_objects_grounding_once(int daxChars)
        {
            using var f = await LongDaxModelAsync(daxChars);
            var whole = await f.Engine.GetWordsQueueAsync();          // everything, for the truth to page against
            var expected = whole.Items.Select(i => i.RuleId + "|" + i.ObjectRef).ToList();

            var seen = new List<string>();
            int? offset = 0;
            var pages = 0;
            while (offset != null)
            {
                var page = await McpTools.GetWordsQueue(f.Engine, 0, offset.Value);      // 0: no size named, the default page
                pages++;
                Assert.NotEmpty(page.Items);
                Assert.Equal(whole.Total, page.Total);
                var text = JsonSerializer.Serialize(page, Web);
                Assert.True(text.Length <= ReadLimit, "page " + pages + " at " + daxChars + " characters of DAX is " + text.Length
                    + " characters: the person's assistant cannot read it in one result.");
                // Each object's grounding is on the page once, whatever number of its items are.
                foreach (var g in page.Grounding.Values.Where(g => g.Expression != null))
                    Assert.Equal(1, CountOf(text, g.Expression));
                Assert.All(page.Items, i => Assert.True(page.Grounding.ContainsKey(i.ObjectRef), i.ObjectRef + " has no grounding on its page"));
                seen.AddRange(page.Items.Select(i => i.RuleId + "|" + i.ObjectRef));
                offset = page.Next;
                Assert.True(pages < 500, "paging does not end");
            }
            Assert.Equal(expected, seen);                              // the pages add up to the whole list, in order
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_object_larger_than_the_budget_still_gets_a_page_of_its_own()
        {
            using var f = await LongDaxModelAsync(60_000, 6);
            var page = await McpTools.GetWordsQueue(f.Engine);
            Assert.NotEmpty(page.Items);
            Assert.NotNull(page.Next);
            // One formula of 60,000 characters is cut on the page, with a note that says where the whole one is.
            var chars = JsonSerializer.Serialize(page, Web).Length;
            Assert.True(chars <= ReadLimit, "a page of one huge formula is " + chars + " characters");
            Assert.Contains(page.Grounding.Values, g => g.Expression != null && g.Expression.Contains("get_grounding"));
        }

        private static int CountOf(string haystack, string needle)
        {
            // The expression is JSON-escaped in the page, so count its escaped form.
            var escaped = JsonSerializer.Serialize(needle, Web).Trim('"');
            var n = 0;
            for (var at = haystack.IndexOf(escaped, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(escaped, at + escaped.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        // ---- L9 -----------------------------------------------------------------------------------------------

        // 12 tables of 25 columns and 4 measures each, cryptic names, nothing described: the size that read 211,951 characters.
        private static async Task<DecisionFixture> TwelveTablesAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Twelve", 1604);
            for (var t = 0; t < 12; t++)
            {
                var table = "TBL_" + (char)('A' + t);
                await f.Engine.CreateTableAsync(table, "human");
                for (var c = 0; c < 25; c++)
                {
                    var type = c % 5 == 0 ? "Int64" : c % 5 == 1 ? "String" : c % 5 == 2 ? "Decimal" : c % 5 == 3 ? "DateTime" : "Int64";
                    await f.Engine.CreateColumnAsync("table:" + table, $"{table}_COL_{c:00}_{(c % 3 == 0 ? "KEY" : c % 3 == 1 ? "AMT" : "CNT")}", type, null, "human");
                }
                for (var m = 0; m < 4; m++)
                    await f.Engine.CreateMeasureAsync("table:" + table, $"{table}_MEAS_{m}",
                        m % 2 == 0 ? $"SUM ( '{table}'[{table}_COL_07_AMT] )" : $"COUNTROWS ( '{table}' )", "human");
            }
            return f;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_first_read_of_the_plan_on_a_large_model_fits_an_assistants_read_and_names_every_row()
        {
            using var f = await TwelveTablesAsync();
            var full = await f.Engine.GetModelPlanAsync();
            var first = await McpTools.GetModelPlan(f.Engine);
            var text = JsonSerializer.Serialize(first, Web);
            Assert.True(text.Length <= ReadLimit, "the first read is " + text.Length + " characters (the whole plan is " + Size(full)
                + "; rows: " + string.Join(", ", full.Items.Select(i => i.Id + "=" + Size(i)).Take(12)) + ")");

            // Every row is named, with its state and its counts, and nothing about a row is silently lost.
            Assert.Equal(full.Items.Select(i => i.Id), first.Items.Select(i => i.Id));
            foreach (var (a, b) in full.Items.Zip(first.Items))
            {
                Assert.Equal(a.StateWord, b.StateWord);
                Assert.Equal(a.Doer, b.Doer);
                Assert.Equal(a.Touches, b.Touches);
                Assert.Equal(a.Title, b.Title);
                var d = first.Detail.Single(x => x.ItemId == a.Id);
                Assert.False(d.Shown, a.Id + " does not say its detail was left out");
                Assert.True(d.Lines >= a.Diff.Length);               // the count of lines its detail holds
                Assert.Empty(b.Diff);
            }
            Assert.Equal(full.ReadyDigest, first.ReadyDigest);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_rows_detail_is_paged_on_request_and_adds_up_to_the_whole_row()
        {
            using var f = await TwelveTablesAsync();
            var full = await f.Engine.GetModelPlanAsync();
            var row = full.Items.OrderByDescending(i => i.Tables.Sum(t => t.Diff.Length)).First();
            Assert.True(row.Tables.Max(t => t.Diff.Length) > 20, "the largest row has " + row.Tables.Max(t => t.Diff.Length) + " lines in a table");
            var perTable = row.Tables.ToDictionary(t => t.Name, t => new List<string>());

            var lines = new List<string>();
            int? offset = 0;
            while (offset != null)
            {
                var page = await McpTools.GetModelPlan(f.Engine, row.Id, offset.Value, 20);
                var item = page.Items.Single(i => i.Id == row.Id);
                var d = page.Detail.Single(x => x.ItemId == row.Id);
                Assert.True(d.Shown);
                Assert.True(Size(page) <= ReadLimit, "a detail page is " + Size(page) + " characters");
                Assert.True(item.Diff.Length <= 20);
                Assert.True(d.Lines >= row.Tables.Max(t => t.Diff.Length));
                lines.AddRange(item.Diff);
                foreach (var t in item.Tables) perTable[t.Name].AddRange(t.Diff);
                // Every other row stays short on a detail page.
                Assert.All(page.Items.Where(i => i.Id != row.Id), i => Assert.True(i.Diff.Length == 0 && !page.Detail.Single(x => x.ItemId == i.Id).Shown));
                offset = d.Next;
            }
            Assert.Equal(row.Diff, lines);
            foreach (var t in row.Tables) Assert.Equal(t.Diff, perTable[t.Name]);
        }

        [Fact]
        public void The_plans_history_pages_on_request_and_a_short_read_carries_only_the_newest()
        {
            var view = new ModelPlanView
            {
                Items = new[] { new ModelPlanItem { Id = "a", Diff = new[] { "+ x" } } },
                History = Enumerable.Range(0, 45).Select(n => new ModelPlanHistoryEntry { Text = "entry " + n, ItemId = "a" }).ToArray(),
            };
            var page = PlanReadShape.For(view, "history", 20, 20);
            Assert.Equal(Enumerable.Range(20, 20).Select(n => "entry " + n), page.History.Select(h => h.Text));
            Assert.Contains("of 45", page.DetailNote);
            Assert.Contains("offset = 40", page.DetailNote);
            Assert.True(!page.Detail[0].Shown && page.Items[0].Diff.Length == 0);
            var last = PlanReadShape.For(view, "history", 40, 20);
            Assert.Equal(5, last.History.Length);
            Assert.DoesNotContain("offset =", last.DetailNote);
            // A row that is not there is said so, not silently ignored.
            Assert.Contains("No row has the id nope", PlanReadShape.For(view, "nope", 0, 0).DetailNote);
        }
    }
}
