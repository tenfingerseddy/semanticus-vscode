// The reading card (Kane's pick A, 12 Oct 2026): the Plan's first read of a model, and its one Fix them.
//
// What these tests hold, one line each:
//   the card gives the grade, the grade after the safe fixes and the top issues in plain words, each with who fixes it
//   Fix them is ONE undo step: one Undo puts the grade back, and the writing jobs it gave the assistant can be taken back
//   pressing Fix them again gives the assistant no second job for the same work
//   the assistant has both tools, on the gated MCP door, through the same engine call as the Plan's card
//   the card goes away once the top issues are handled
//   (the open-time warm-up waits for the first grade: BatchToolsTests.The_first_read_runs_before_the_warm_up)

using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ReadingCardTests
    {
        [Fact]
        public async Task The_card_gives_the_grade_the_grade_after_and_the_top_issues_in_plain_words()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("CardRead.bim");
            var card = await f.Engine.ReadingCardAsync(withAfter: true);

            Assert.False(string.IsNullOrEmpty(card.Grade));
            Assert.NotEmpty(card.Top);
            Assert.True(card.Top.Length <= 3);
            Assert.True(card.Show);
            Assert.False(string.IsNullOrEmpty(card.GradeAfter));   // the Plan's own rehearsal of the safe fixes
            Assert.All(card.Top, i =>
            {
                Assert.True(i.Count > 0);
                Assert.Contains(i.Who, new[] { ReadingWho.Semanticus, ReadingWho.Assistant, ReadingWho.Person });
                Assert.DoesNotContain('\u2014', i.Headline + i.Why);   // no em dashes (product copy style)
                Assert.StartsWith(ReadingCardBuilder.Count(i.Count), i.Headline.StartsWith("This ") ? ReadingCardBuilder.Count(i.Count) : i.Headline);
            });
            Assert.Null((await f.Engine.ReadingCardAsync(withAfter: false)).GradeAfter);   // the fast first call leaves it out
        }

        [Fact]
        public async Task Fix_them_is_one_undo_step_and_the_writing_goes_to_the_assistant()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("CardFix.bim");
            var before = await f.Engine.ReadingCardAsync(withAfter: true);
            Assert.True(before.SafeFixCount > 0, "the fixture has a key column that adds up");

            var fix = await f.Engine.FixThemAsync("human");
            Assert.True(fix.Fixed > 0);
            Assert.Equal(before.Grade, fix.GradeBefore);
            Assert.Equal(before.GradeAfter, fix.GradeAfter);   // the rehearsal told the truth: the real grade after is the grade it promised
            Assert.NotEmpty(fix.Jobs);
            Assert.All(fix.Jobs, j => Assert.StartsWith("pt-", j.RowId));

            // The jobs are assistant tasks on the plan, with the findings in their brief.
            var plan = await f.Engine.GetModelPlanAsync();
            foreach (var job in fix.Jobs)
            {
                var row = plan.Items.Single(i => i.Id == job.RowId);
                Assert.Equal(PlanDoers.Assistant, row.Doer);
            }

            // One Undo puts the model back: the same grade and the same safe fixes are open again.
            await f.Engine.UndoAsync("human");
            var back = await f.Engine.ReadingCardAsync(withAfter: false);
            Assert.Equal(before.Grade, back.Grade);
            Assert.Equal(before.Score, back.Score);
            Assert.Equal(before.SafeFixCount, back.SafeFixCount);

            // The writing jobs are taken back while the assistant has not started them.
            foreach (var job in fix.Jobs) await f.Engine.RemovePlanRowAsync(job.RowId);
            plan = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(plan.Items, i => fix.Jobs.Any(j => j.RowId == i.Id) && i.Status != ModelPlanStatuses.Skipped);
        }

        [Fact]
        public async Task Pressing_Fix_them_again_gives_the_assistant_no_second_job_for_the_same_writing()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("CardTwice.bim");
            var first = await f.Engine.FixThemAsync("human");
            Assert.NotEmpty(first.Jobs);
            var card = await f.Engine.ReadingCardAsync(withAfter: false);
            Assert.All(card.Top.Where(i => i.Who == ReadingWho.Assistant), i => Assert.Equal("queued", i.State));

            var second = await f.Engine.FixThemAsync("human");
            Assert.Empty(second.Jobs);
            var open = (await f.Engine.GetModelPlanAsync()).Items.Where(i => first.Jobs.Any(j => j.RowId == i.Id)).ToArray();
            Assert.Equal(first.Jobs.Length, open.Length);
        }

        [Fact]
        public async Task The_card_goes_away_once_the_top_issues_are_handled()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("CardGone.bim");
            Assert.True((await f.Engine.ReadingCardAsync(withAfter: false)).Show);
            await f.Engine.FixThemAsync("human");
            var card = await f.Engine.ReadingCardAsync(withAfter: false);
            // Safe fixes are applied and the writing is on the assistant's plan: nothing on the top is open for Semanticus or the assistant.
            Assert.False(card.Show, string.Join(" | ", card.Top.Select(i => i.Headline + " [" + i.Who + "," + i.State + "]")));
        }

        // The ranking, from a scorecard alone (no model): the grade's holder first, what only the person decides last, and a fix Semanticus can make is
        // always among the three, because Fix them does both things and the card names both.
        [Fact]
        public void The_top_three_rank_the_grade_holder_first_keep_the_persons_decisions_last_and_name_a_fix_Semanticus_makes()
        {
            Semanticus.Analysis.ReadinessFinding F(string rule, string fix, string sev, int n) => new Semanticus.Analysis.ReadinessFinding
                { RuleId = rule, RuleTitle = rule + " title", Fix = fix, Severity = sev, ObjectRef = "column:T/" + n, ObjectName = "c" + n };
            var findings = Enumerable.Range(0, 5).Select(i => F("DESC-MEASURE", "AiContent", "High", i))
                .Concat(Enumerable.Range(0, 50).Select(i => F("SYN-FIELD", "AiContent", "Medium", i)))
                .Concat(Enumerable.Range(0, 40).Select(i => F("NAME-DUP", "Proposal", "High", i)))
                .Concat(Enumerable.Range(0, 30).Select(i => F("FMT-MEASURE", "AiContent", "Medium", i)))
                .Concat(Enumerable.Range(0, 3).Select(i => F("VIS-FK", "SafeFix", "Medium", i))).ToArray();
            var card = new Semanticus.Analysis.Scorecard { Grade = "F", Overall = 50, Findings = findings, SafeFixCount = 3,
                GatedBy = new[] { ">50% of visible measures undescribed; capped at D" } };
            var view = ReadingCardBuilder.Build(card, new System.Collections.Generic.HashSet<string>(), 1);
            Assert.Equal("desc-measure", view.Top[0].Key);
            Assert.True(view.Top[0].HoldsGrade);
            Assert.Contains(view.Top, i => i.Who == ReadingWho.Semanticus);          // the 3 key columns, though the smallest group
            Assert.DoesNotContain(view.Top, i => i.Key == "same-name");              // the person's decision never crowds out what the card can do
            Assert.True(view.Show);

            // Once the assistant has the writing and the safe fixes are made, nothing on the top is open for the card, so it goes away.
            var queued = new System.Collections.Generic.HashSet<string> { ReadingCardBuilder.WritingSendKey("descriptions"), ReadingCardBuilder.WritingSendKey("other-names"), ReadingCardBuilder.WritingSendKey("other-writing") };
            card.Findings = findings.Where(f => f.Fix != "SafeFix").ToArray();
            Assert.False(ReadingCardBuilder.Build(card, queued, 1).Show);
        }

        [Fact]
        public async Task The_assistant_has_both_tools_on_the_gated_door_and_the_card_is_one_engine_call()
        {
            Assert.NotNull(typeof(EngineRpcTarget).GetMethod("readingCard"));
            Assert.NotNull(typeof(EngineRpcTarget).GetMethod("fixThem"));
            Assert.Null(typeof(HumanGovernanceRpcTarget).GetMethod("fixThem"));
            string[] names = { "reading_card", "fix_them" };
            foreach (var n in names)
                Assert.Single(typeof(McpTools).Assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    .Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == n));

            using var f = await ChangeCauseTests.PlanModelAsync("CardMcp.bim");
            var gated = AssistantEngine.ForAssistant(f.Engine);
            var read = await McpTools.ReadingCard(gated);
            Assert.Equal((await f.Engine.ReadingCardAsync(true)).Grade, read.Grade);
            var fix = await McpTools.FixThem(gated);
            Assert.True(fix.Fixed > 0);
            Assert.NotEmpty(fix.Jobs);
        }
    }
}
