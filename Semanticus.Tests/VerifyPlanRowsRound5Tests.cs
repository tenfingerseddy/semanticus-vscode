// Verifier's failing tests for lane m11/plan-rows, round 5, at 21246fe4 (29 Sep): what the round-5 answers on the agent
// door still get wrong (lane-reports/v-plan-rows-r5.md). None of them writes anything wrong to a model. Left failing on
// purpose; the fix makes them pass.
//
//   R5-1  every answer the agent door gives to a change to the plan is at most 40,000 characters, on any model. The reads
//         keep that with a catch-all that cuts any one long text (PlanReadShape.CutLongStrings); the answers do not have
//         one. A row set aside with a long reason carries the reason twice in the skip answer (the row's skipReason and the
//         newest history entry's first line), and the answers after it carry it again in their history until three newer
//         entries push it out: a 45,000-character reason makes the skip answer 91,372 characters, the next unskip 46,776 and
//         the propose after that 46,930.
//   R5-2  a run that leaves out more than its answer lists ends the list with a note that says "the rows' own reads
//         (get_model_plan item = the row's id) show what each row left out". They do not: after the run no row's refused
//         list holds any fix the answer did not list. On 400 ratio measures and 40 Double columns the answer lists 53 of
//         the 440 left out, every one a ratio format; the 40 data type changes are all behind the cap, and of what the
//         note points at only bpa_scan names them (885,741 characters whole on a 42-table hub; filtered by rule it fits).
//   R5-3  setting aside a row that is done answers "... was not set aside." while the same answer's newest history entry
//         says "Set aside ..." with the reason, and the set-aside is kept on the plan.
//   R5-4  (low) a words page counts escapes, but the grounding's cut stops at 100 characters a text: 50 sibling measures
//         whose 500-character names are full of ' and + (six characters each on the wire) give 100 of 101 default pages
//         over 40,000, the largest 45,751. The item's own objectRef and objectName, and the grounding's key, are never cut.
//
// Integration (m11 join, group G1) changed two of these on purpose, each to what the fix now does and no weaker:
//   R5-1  a set-aside reason is now capped at PlanTasks.MaxNoteChars like a pause reason, so the 45,000-character skip is
//         refused; the long reason reaches the answers through the memory file instead (a file an uncapped build wrote),
//         which is what the catch-all over the short answers must still hold to 40,000.
//   R5-2  on the joined build the ratio formats are no longer left out and 40 data type changes fit the answer, so the
//         fixture carries 200 long-named Double columns to reach the cap; the note now names each rule and bpa_scan, and
//         the test checks that read lists every fix the answer left out, instead of the rows' reads it no longer names.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyPlanRowsRound5Tests
    {
        private const int Limit = 40_000;

        // What the MCP door sends is the tool's return value serialized with the SDK's own options (escapes counted).
        private static int WireChars(object o) => JsonSerializer.Serialize(o, ModelContextProtocol.McpJsonUtilities.DefaultOptions).Length;

        private static Task<DecisionFixture> SmallModelAsync(string file) => PlanRowsBim.OpenAsync("SetAside", file,
            new object[]
            {
                PlanRowsBim.Table("Sales", new[] { "Amount:Decimal", "Qty:Int64", "CustomerKey:Int64" }, PlanRowsBim.M("Revenue", "SUM ( Sales[Amount] )")),
                PlanRowsBim.Table("Customer", new[] { "CustomerKey:Int64", "Name:String" }),
            },
            new object[] { PlanRowsBim.Rel("Sales", "CustomerKey", "Customer", "CustomerKey") });

        [Fact]
        public async Task R5_1_every_plan_answer_stays_at_40000_when_a_row_is_set_aside_with_a_long_reason()
        {
            using var f = await SmallModelAsync("R5LongReason.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var first = plan.Items[0].Id;
            var other = plan.Items.Skip(1).First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal)
                                                   || i.Id == "bpafix/model" || i.Id == "cleanup/model").Id;
            // A reason pasted in from a review: plain words, no character the door escapes.
            var reason = string.Concat(Enumerable.Repeat("The person set this aside after a long review; it waits for the new data source. ", 600))
                .Substring(0, 45_000);

            // THE FIX, first half (integration, joinfix G1): a set-aside reason is capped like a pause reason and a note
            // (PlanTasks.MaxNoteChars), so this reason is refused on both doors and nothing is kept.
            var refused = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SkipModelPlanItem(f.Engine, first, reason));
            Assert.Contains(PlanTasks.MaxNoteChars.ToString(), refused.Message, StringComparison.Ordinal);
            // The second half, the catch-all this test was written for: a reason already kept by a build that had no cap (a
            // memory file travels with the project) still reaches every answer, and each must stay at 40,000.
            Assert.True(f.Engine.SeedPlanMemoryForTest(state =>
            {
                state.Skips.Add(new PlanSkip { ItemId = first, Reason = reason, When = DateTimeOffset.UtcNow });
                state.History.Add(new ModelPlanHistoryEntry
                {
                    When = DateTimeOffset.UtcNow, Kind = ModelPlanHistoryKinds.Skipped, Actor = ModelPlanActors.You, ItemId = first,
                    Sentence = "Set aside " + first + ".", Text = "Set aside " + first + ".", Detail = new[] { reason },
                });
            }));

            var sizes = new List<(string What, int Chars)>
            {
                ("skip again with a short reason", WireChars(await McpTools.SkipModelPlanItem(f.Engine, first, reason.Substring(0, 900)))),
            };
            Assert.True(f.Engine.SeedPlanMemoryForTest(state => state.Skips.Single(k => k.ItemId == first).Reason = reason));
            sizes.Add(("propose " + other, WireChars(await McpTools.ModelPlanAction(f.Engine, other, "propose"))));
            sizes.Add(("cancel " + other, WireChars(await McpTools.ModelPlanAction(f.Engine, other, "cancel"))));
            sizes.Add(("unskip", WireChars(await McpTools.UnskipModelPlanItem(f.Engine, first))));
            var over = sizes.Where(s => s.Chars > Limit).ToList();
            Assert.True(over.Count == 0, over.Count + " of " + sizes.Count + " answers after a 45,000-character reason are over 40,000 characters: "
                + string.Join("; ", over.Select(s => s.What + " " + s.Chars))
                + ". The answer carries the reason whole in the row's skipReason and in its newest history entries; nothing cuts it.");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task R5_2_a_run_that_leaves_out_more_than_it_lists_points_at_reads_that_name_the_rest()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var columns = new List<object> { new Dictionary<string, object> { ["name"] = "Amount", ["dataType"] = "decimal", ["sourceColumn"] = "Amount" } };
            // 200 Double columns with long names (integration, joinfix G1): on the joined build the 400 ratio formats are no
            // longer lines the run leaves out, and 40 data type changes fit in the answer, so the list is made long enough to cap.
            columns.AddRange(Enumerable.Range(0, 200).Select(k => (object)new Dictionary<string, object>
                { ["name"] = "Rate " + k.ToString("000") + " " + new string('y', 80), ["dataType"] = "double", ["sourceColumn"] = "Rate " + k.ToString("000") }));
            var measures = Enumerable.Range(0, 400).Select(m => new Dictionary<string, object>
                { ["name"] = "Ratio " + m.ToString("000") + " " + new string('x', 60), ["expression"] = "DIVIDE ( SUM ( Sales[Amount] ), " + (m + 3) + " )" }).ToArray();
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
                            ["name"] = "Sales", ["columns"] = columns, ["measures"] = measures,
                            ["partitions"] = new object[] { new Dictionary<string, object> { ["name"] = "Sales", ["mode"] = "import",
                                ["source"] = new Dictionary<string, object> { ["type"] = "m", ["expression"] = "let\n    Source = #table({}, {})\nin\n    Source" } } },
                        },
                    },
                },
            };
            var path = System.IO.Path.Combine(f.Workspace, "R5Ratios.bim");
            await System.IO.File.WriteAllTextAsync(path, JsonSerializer.Serialize(bim));
            await f.Engine.OpenAsync(path);
            f.UseNull();
            await f.Engine.ProposePlanAsync(null, true, 40, "agent");
            var plan = await f.Engine.GetModelPlanAsync();

            // The run once, whole, and the agent door's answer to it (the door is this projection of the same result).
            var whole = await f.Engine.ApplyPlanItemsAsync(null, "agent", plan.ReadyDigest);
            var answer = PlanResultShape.Of(whole);
            Assert.True(whole.Performed, whole.Sentence);
            Assert.False(string.IsNullOrEmpty(answer.SkippedNote), "the fixture must leave out more than the answer lists; it listed all " + answer.Skipped?.Length);
            // The note used to send the reader to the rows' own reads, which do not carry what a run left out. It must point
            // only where the rest truly is: a best-practice fix left out is still a finding, so bpa_scan filtered by its rule,
            // which the note must name, lists it.
            Assert.DoesNotContain("rows' own reads", answer.SkippedNote, StringComparison.Ordinal);
            var unlisted = whole.Skipped.Skip(answer.Skipped.Length).Where(s => !string.IsNullOrEmpty(s.What)).ToList();
            var dataTypes = unlisted.Count(s => (s.Reason ?? "").Contains("data type", StringComparison.OrdinalIgnoreCase));
            Assert.True(dataTypes > 0, "the premise: data type changes are behind the cap");

            // The object's own name (the last [..] of "'Sales'[Rate 000 yyy]"), however a read spells the rest.
            static string NameOf(string what) =>
                System.Text.RegularExpressions.Regex.Match(what, @"\[([^\]]+)\]\s*$") is { Success: true } m ? m.Groups[1].Value : what;
            var found = new List<Semanticus.Analysis.BpaViolation>();
            foreach (var rule in unlisted.Select(s => s.RuleId).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Assert.True(answer.SkippedNote.Contains(rule, StringComparison.Ordinal), "the note does not name the rule " + rule + ": " + answer.SkippedNote);
                found.AddRange((await McpTools.BpaScan(f.Engine, ruleId: rule)).Violations);
            }
            var shown = unlisted.Count(s => s.RuleId != null && found.Any(v => (v.ObjectRef ?? "").Contains(NameOf(s.What), StringComparison.Ordinal)
                                                                            || (v.ObjectName ?? "").Contains(NameOf(s.What), StringComparison.Ordinal)));
            Assert.True(shown == unlisted.Count, "the answer lists " + answer.Skipped.Length + " of the " + whole.Skipped.Length
                + " left out; " + shown + " of the " + unlisted.Count + " not listed are in the read its note names ("
                + dataTypes + " of the unlisted are data type changes). Note: " + answer.SkippedNote);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task R5_3_setting_aside_a_row_that_is_done_answers_what_the_plan_records()
        {
            using var f = await SmallModelAsync("R5DoneRow.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(ran.Performed, ran.Sentence);
            var now = await f.Engine.GetModelPlanAsync();
            var done = now.Items.FirstOrDefault(i => i.StateWord == "done");
            Assert.True(done != null, "the fixture must have a done row after Run the plan: " + string.Join(", ", now.Items.Select(i => i.Id + " " + i.StateWord)));

            var answer = await McpTools.SkipModelPlanItem(f.Engine, done.Id, "Not this quarter.");
            var newest = answer.History?.LastOrDefault();
            var recorded = newest != null && newest.Kind == ModelPlanHistoryKinds.Skipped && newest.ItemId == done.Id;
            Assert.False(recorded && answer.Sentence.Contains("was not set aside", StringComparison.Ordinal),
                "the answer says: " + answer.Sentence + " Its newest history entry says: " + newest?.Sentence + " (" + string.Join(" ", newest?.Detail ?? Array.Empty<string>()) + ").");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task R5_4_a_words_page_stays_at_40000_when_sibling_names_are_long_and_full_of_characters_the_door_escapes()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("EscapedNames", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            // Names stop at 512 characters; each of these is 500, and the door writes each ' and + as six.
            for (var k = 0; k < 50; k++)
                await f.Engine.CreateMeasureAsync("table:Sales", "Sib" + k.ToString("00") + " " + string.Concat(Enumerable.Repeat("'+", 250)).Substring(0, 496),
                    "COUNTROWS ( Sales )", "human");

            var sizes = new List<(int Offset, int Chars)>();
            int? offset = 0;
            while (offset != null && sizes.Count < 300)
            {
                var page = await McpTools.GetWordsQueue(f.Engine, 0, offset.Value);      // the default page
                sizes.Add((offset.Value, WireChars(page)));
                offset = page.Next;
            }
            var over = sizes.Where(x => x.Chars > Limit).ToList();
            Assert.True(over.Count == 0, over.Count + " of " + sizes.Count + " default words pages are over 40,000 characters, the largest "
                + (over.Count == 0 ? 0 : over.Max(x => x.Chars)) + " (offsets " + string.Join(", ", over.Take(5).Select(x => x.Offset)) + " ...).");
        }
    }
}
