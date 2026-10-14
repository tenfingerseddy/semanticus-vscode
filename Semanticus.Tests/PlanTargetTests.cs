// The target: what this model should become (the Fix milestone, One Plan option A).
//
// What these tests are really holding, in one line each:
//   there is always one rule-derived aim per section, and it is the section's own why line as a statement
//   the why lines here are the ones the page shows, word for word, or this test fails instead of drifting
//   a target the person typed is remembered beside the model and survives closing it
//   a typed target becomes exactly ONE item, in its own section, with an empty diff and an honest sub line
//   the item's jump is the ask box's own word routing, and no clear winner means no link rather than a guess
//   removing the target removes the item; marking it done changes nothing in the model and says so
//   the worth's target line says the grade now, the grade the fixes reach, and only then what was asked for
//   both doors carry the same three operations, and every one of them is free
//
// The fixture is DecisionFixture, the same one ModelPlanTests uses, for the same reason: a target is kept
// beside the model, so the model needs a durable on-disk identity before one can be kept at all.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanTargetTests
    {
        /// <summary>The same small gappy model ModelPlanTests uses: enough real findings that the plan has
        /// items in more than one section, so a target can be proved to land in the right one.</summary>
        private static async Task<DecisionFixture> GappyModelAsync(string file = "Target.bim", bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();   // no service: every test here is the free, no-key path unless it says otherwise
            return f;
        }

        /// <summary>A target whose words land clearly on Advanced modelling, which is where a calendar is
        /// actually made. Used wherever a test needs a REAL routing result rather than a stubbed one.</summary>
        private const string CalendarTarget = "Mark a date table and add a fiscal calendar";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        // ---- the rule-derived half -------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_rules_give_one_aim_per_section_and_none_of_them_is_empty()
        {
            using var f = await GappyModelAsync("TargetAims.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var aims = plan.Targets.Where(t => t.Source == ModelPlanTargetSources.Rules).ToArray();

            Assert.Equal(ModelPlanSections.InOrder.Length, aims.Length);
            Assert.Equal(ModelPlanSections.InOrder, aims.Select(a => a.Section).ToArray());
            Assert.All(aims, a =>
            {
                Assert.False(string.IsNullOrWhiteSpace(a.Text));
                Assert.DoesNotContain("—", a.Text, StringComparison.Ordinal);
                Assert.Null(a.When);
                Assert.Null(a.ItemId);   // an aim is not an item; only a typed target becomes one
            });
        }

        [Fact]
        public void The_aims_are_built_from_the_why_lines_the_page_actually_shows()
        {
            // The engine ships without the webview and has to name the sections the same way headless, so
            // the why lines are COPIED. This holds the copy to plan-model.ts itself, the same way
            // AskRouteCatalogTests holds the router's guide lines to help.tsx.
            var src = File.ReadAllText(Path.Combine(
                RepoRoot(), "Semanticus.VSCode", "webview", "src", "plan-model.ts"));
            var page = Regex.Matches(src, @"\{\s*name:\s*'([^']+)',\s*why:\s*'([^']+)'\s*\}")
                .Select(m => (Name: m.Groups[1].Value, Why: m.Groups[2].Value))
                .ToArray();

            Assert.Equal(ModelPlanSections.InOrder.Length, page.Length);
            foreach (var (name, why) in page)
                Assert.Equal(why, PlanTargets.WhyOf(name));
        }

        [Fact]
        public void An_aim_is_never_missing_for_a_section_the_plan_can_file_an_item_under()
        {
            foreach (var section in ModelPlanSections.InOrder)
            {
                Assert.False(string.IsNullOrWhiteSpace(PlanTargets.WhyOf(section)), section);
                Assert.Contains(PlanTargets.RuleAims(), a => a.Section == section && !string.IsNullOrWhiteSpace(a.Text));
            }

            Assert.Null(PlanTargets.WhyOf("Nonsense"));
        }

        // ---- a typed target is remembered ---------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_target_you_typed_is_still_there_when_the_model_is_opened_again()
        {
            using var f = await GappyModelAsync("TargetRoundTrip.bim");

            var added = await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");
            var mine = Assert.Single(added.Targets.Where(t => t.Source == ModelPlanTargetSources.You));
            Assert.Equal(CalendarTarget, mine.Text);
            Assert.Equal(ModelPlanSections.Structure, mine.Section);
            Assert.NotNull(mine.When);

            // Close it and open it again: the target lives in the sidecar under the model's own identity,
            // so a reopen is the real proof and an in-memory field would not be.
            await f.Engine.CreateModelAsync("Somewhere Else", 1604);
            await f.Engine.OpenAsync(Path.Combine(f.Workspace, "TargetRoundTrip.bim"));
            f.UseNull();

            var back = await f.Engine.GetModelPlanAsync();
            var again = Assert.Single(back.Targets.Where(t => t.Source == ModelPlanTargetSources.You));
            Assert.Equal(mine.Id, again.Id);
            Assert.Equal(CalendarTarget, again.Text);
            Assert.Equal(ModelPlanSections.Structure, again.Section);
            Assert.Equal(mine.Id, again.ItemId);   // an aim task is its own item (contract 15)
        }

        [Fact]
        public void The_sidecar_keeps_a_target_and_reads_it_back_whole()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-plan-target-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var file = PlanMemory.FileIn(dir);
                var when = new DateTimeOffset(2026, 9, 22, 4, 5, 6, TimeSpan.Zero);

                Assert.True(PlanMemory.Mutate(file, "disk:abc", state => state.Targets.Add(new ModelPlanTarget
                {
                    Id = "every-measure-has-a-check",
                    Text = "Every measure has a check",
                    Section = ModelPlanSections.Trust,
                    Source = ModelPlanTargetSources.You,
                    When = when,
                })));

                var read = PlanMemory.Read(file, "disk:abc");
                var target = Assert.Single(read.Targets);
                Assert.Equal("every-measure-has-a-check", target.Id);
                Assert.Equal("Every measure has a check", target.Text);
                Assert.Equal(ModelPlanSections.Trust, target.Section);
                Assert.Equal(when, target.When);
                Assert.Null(target.DoneUtc);

                // Another model in the same folder does not inherit it, which is the whole point of the key.
                Assert.Empty(PlanMemory.Read(file, "disk:other").Targets);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---- a typed target becomes an item ---------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_target_becomes_one_item_at_the_head_of_its_own_section()
        {
            using var f = await GappyModelAsync("TargetItem.bim");
            var before = await f.Engine.GetModelPlanAsync();
            var openBefore = before.Items.Length;

            var plan = await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");

            var target = Assert.Single(plan.Targets.Where(t => t.Source == ModelPlanTargetSources.You));
            var item = Assert.Single(plan.Items.Where(i => i.Status == ModelPlanStatuses.Target));

            // Since Plan at the heart (contract 15) a typed target is an aim TASK: the item's id is the task's
            // random id, the same id the target list carries, and the old "target/slug" id lives on as an alias.
            Assert.Equal(target.Id, item.Id);
            Assert.Matches("^pt-[0-9a-f]{8}$", item.Id);
            Assert.Equal(ModelPlanSections.Structure, item.Section);
            Assert.Equal(CalendarTarget, item.Title);
            Assert.Equal(ModelPlanOwners.You, item.Who);
            // The Plan wording moved from target to aim (m14 f10bbe2b).
            Assert.Contains("Nothing in the model does it yet", item.Sub, StringComparison.OrdinalIgnoreCase);
            // An empty diff here means "the plan does not know the change", not "there is nothing to do",
            // and inventing a line would be the plan claiming knowledge it does not have.
            Assert.Empty(item.Diff);
            Assert.NotEmpty(item.Why);
            Assert.All(item.Why, w => Assert.DoesNotContain("—", w, StringComparison.Ordinal));

            // It is the FIRST thing in its section: the target is what the section is for.
            Assert.Equal(item.Id, plan.Items.First(i => i.Section == ModelPlanSections.Structure).Id);

            // And it is counted: the plan is one item longer than it was.
            Assert.Equal(openBefore + 1, plan.Items.Length);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_item_points_where_the_ask_box_would_send_the_same_words()
        {
            using var f = await GappyModelAsync("TargetJump.bim");

            var plan = await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");
            var item = Assert.Single(plan.Items.Where(i => i.Status == ModelPlanStatuses.Target));

            // Not a hand-written expectation: the routing the box itself would do on the same sentence.
            var routed = AskRoutePoint.MatchWords(CalendarTarget);
            Assert.NotNull(routed);
            Assert.NotNull(item.Jump);
            Assert.Equal(routed!.Tool, item.Jump.Tool);
            Assert.Equal(AskRouteCatalog.Find(routed.Tool).Label, item.Jump.Label);
            Assert.Contains(item.Jump.Label, string.Join(" ", item.Why), StringComparison.Ordinal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_target_whose_words_name_no_place_carries_no_link_rather_than_a_guess()
        {
            using var f = await GappyModelAsync("TargetNoJump.bim");
            const string vague = "make everything lovely";
            Assert.Null(AskRoutePoint.MatchWords(vague));   // the premise: the box would not place it either

            var plan = await f.Engine.AddPlanTargetAsync(vague, ModelPlanSections.Meaning, "human");
            var item = Assert.Single(plan.Items.Where(i => i.Status == ModelPlanStatuses.Target));

            Assert.Null(item.Jump);
            Assert.Contains("do not name a page", string.Join(" ", item.Why), StringComparison.Ordinal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Typing_the_same_target_twice_is_one_target()
        {
            using var f = await GappyModelAsync("TargetTwice.bim");

            await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");
            var plan = await f.Engine.AddPlanTargetAsync(CalendarTarget.ToUpperInvariant(), ModelPlanSections.Ship, "human");

            var mine = Assert.Single(plan.Targets.Where(t => t.Source == ModelPlanTargetSources.You));
            Assert.Equal(ModelPlanSections.Ship, mine.Section);   // refiled where they put it this time
            Assert.Single(plan.Items.Where(i => i.Status == ModelPlanStatuses.Target));
        }

        // ---- removing and finishing -------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Removing_the_target_removes_the_item_with_it()
        {
            using var f = await GappyModelAsync("TargetRemove.bim");

            var added = await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");
            var item = Assert.Single(added.Items.Where(i => i.Status == ModelPlanStatuses.Target));
            var count = added.Items.Length;

            // The ITEM's id, not the target's: a person is shown both, so both have to work.
            var after = await f.Engine.RemovePlanTargetAsync(item.Id, "human");

            Assert.Empty(after.Targets.Where(t => t.Source == ModelPlanTargetSources.You));
            Assert.DoesNotContain(after.Items, i => i.Id == item.Id);
            Assert.Equal(count - 1, after.Items.Length);
            Assert.Equal(ModelPlanSections.InOrder.Length, after.Targets.Length);   // the aims stay
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Only_a_target_you_typed_can_be_removed()
        {
            using var f = await GappyModelAsync("TargetRulesKept.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var aim = plan.Targets.First(t => t.Source == ModelPlanTargetSources.Rules);

            var refused = await Assert.ThrowsAsync<ArgumentException>(
                () => f.Engine.RemovePlanTargetAsync(aim.Id, "human"));

            // Wording moved from target to aim (m14 f10bbe2b).
            Assert.Contains("You can remove only the aims you added", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("—", refused.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Marking_a_target_done_leaves_it_on_the_list_and_changes_nothing_in_the_model()
        {
            using var f = await GappyModelAsync("TargetDone.bim");
            var added = await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");
            var item = Assert.Single(added.Items.Where(i => i.Status == ModelPlanStatuses.Target));
            var revision = f.Sessions.Current.Revision;

            var result = await f.Engine.MarkPlanTargetDoneAsync(item.Id, "human");

            Assert.Equal(PlanTargets.DoneVerb, result.Action);
            // Performed means the MODEL changed. It did not: this is the person's own note that they
            // finished the work by hand, so claiming true would report an edit nobody made.
            Assert.False(result.Performed);
            Assert.Equal(revision, f.Sessions.Current.Revision);
            Assert.Contains("Nothing in the model changed", result.Sentence, StringComparison.Ordinal);

            var mine = Assert.Single(result.Plan.Targets.Where(t => t.Source == ModelPlanTargetSources.You));
            Assert.NotNull(mine.DoneUtc);
            var done = Assert.Single(result.Plan.Items.Where(i => i.Id == item.Id));
            Assert.Equal(ModelPlanStatuses.Done, done.Status);
            Assert.Empty(result.Plan.Items.Where(i => i.Status == ModelPlanStatuses.Target));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Marking_something_that_is_not_your_target_done_is_refused_in_words()
        {
            using var f = await GappyModelAsync("TargetDoneUnknown.bim");

            var result = await f.Engine.MarkPlanTargetDoneAsync("target/nothing-like-this", "human");

            Assert.False(result.Performed);
            // Wording moved from target to aim (m14 f10bbe2b).
            Assert.Contains("Only an aim you typed", result.Sentence, StringComparison.Ordinal);
            Assert.NotNull(result.Plan);
        }

        // ---- what it is worth ---------------------------------------------------------------------------

        [Fact]
        public void The_target_line_says_the_grade_now_then_the_fixes_then_what_you_asked_for()
        {
            var stays = new ModelPlanWorth { From = "B", To = "B", Unchanged = true };
            Assert.Equal("Stays B.", PlanTargets.TargetLine(stays, 0));
            Assert.Equal("Stays B, then what you asked for.", PlanTargets.TargetLine(stays, 2));

            var rises = new ModelPlanWorth { From = "B", To = "A" };
            Assert.Equal("B, then A.", PlanTargets.TargetLine(rises, 0));

            // A pending second grade is not a missing one and must not be rounded into a match.
            var pending = new ModelPlanWorth { From = "B", Pending = true };
            Assert.Contains("still being worked out", PlanTargets.TargetLine(pending, 0), StringComparison.Ordinal);
            Assert.DoesNotContain("Stays", PlanTargets.TargetLine(pending, 0), StringComparison.Ordinal);

            // No grade at all says so rather than inventing one.
            Assert.Equal("No grade yet.", PlanTargets.TargetLine(new ModelPlanWorth(), 0));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_worth_on_a_real_plan_carries_the_target_line()
        {
            using var f = await GappyModelAsync("TargetWorth.bim");

            var before = await f.Engine.GetModelPlanAsync();
            Assert.False(string.IsNullOrWhiteSpace(before.Worth.TargetLine));
            Assert.DoesNotContain("what you asked for", before.Worth.TargetLine, StringComparison.Ordinal);
            Assert.DoesNotContain("—", before.Worth.TargetLine, StringComparison.Ordinal);

            var after = await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");
            Assert.Contains("then what you asked for", after.Worth.TargetLine, StringComparison.Ordinal);

            // A target the person marked done is no longer something still asked for.
            var item = Assert.Single(after.Items.Where(i => i.Status == ModelPlanStatuses.Target));
            var done = await f.Engine.MarkPlanTargetDoneAsync(item.Id, "human");
            Assert.DoesNotContain("what you asked for", done.Plan.Worth.TargetLine, StringComparison.Ordinal);
        }

        // ---- what is refused, and who may do it ---------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_target_must_name_one_of_the_five_goals()
        {
            using var f = await GappyModelAsync("TargetSection.bim");

            var refused = await Assert.ThrowsAsync<ArgumentException>(
                () => f.Engine.AddPlanTargetAsync(CalendarTarget, "Vibes", "human"));
            foreach (var section in ModelPlanSections.InOrder)
                Assert.Contains(section, refused.Message, StringComparison.Ordinal);

            // Case is forgiven, because a person types it the way they say it.
            var plan = await f.Engine.AddPlanTargetAsync(CalendarTarget, "structure", "human");
            Assert.Equal(ModelPlanSections.Structure,
                Assert.Single(plan.Targets.Where(t => t.Source == ModelPlanTargetSources.You)).Section);
        }

        [Fact]
        public async Task An_empty_or_overlong_target_is_refused_rather_than_cut()
        {
            using var f = await GappyModelAsync("TargetWords.bim");

            await Assert.ThrowsAsync<ArgumentException>(
                () => f.Engine.AddPlanTargetAsync("   ", ModelPlanSections.Ship, "human"));

            var tooLong = new string('a', PlanTargets.MaxTextChars + 1);
            var refused = await Assert.ThrowsAsync<ArgumentException>(
                () => f.Engine.AddPlanTargetAsync(tooLong, ModelPlanSections.Ship, "human"));
            Assert.Contains(PlanTargets.MaxTextChars.ToString(), refused.Message, StringComparison.Ordinal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_free_session_can_say_what_its_model_should_become()
        {
            using var f = await GappyModelAsync("TargetFree.bim", pro: false);

            var plan = await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");

            Assert.Single(plan.Targets.Where(t => t.Source == ModelPlanTargetSources.You));
            Assert.Single(plan.Items.Where(i => i.Status == ModelPlanStatuses.Target));
            Assert.Equal(ModelPlanSections.InOrder.Length + 1, plan.Targets.Length);
        }

        [Fact]
        public void An_id_is_built_from_the_words_and_never_comes_back_empty()
        {
            Assert.Equal("every-measure-has-a-check", PlanTargets.IdFor("Every measure has a check"));
            Assert.Equal(PlanTargets.IdFor("Every measure has a check"),
                         PlanTargets.IdFor("  EVERY   measure has a check!  "));
            // Words that survive nothing still get an id, and it is a hash of the text rather than a
            // counter, so it is the same on every machine and different for different words.
            var punctuation = PlanTargets.IdFor("!!! ??? ...");
            Assert.StartsWith("t", punctuation, StringComparison.Ordinal);
            Assert.Equal(9, punctuation.Length);                             // "t" plus eight hex digits
            Assert.NotEqual(punctuation, PlanTargets.IdFor("??? !!! ..."));
            // A cut id stays readable and stays bounded: sixty characters of the words, then a dash and
            // eight hex digits of the whole text. The suffix is what keeps two cut ids apart.
            var cut = PlanTargets.IdFor(new string('x', 500));
            Assert.Equal(69, cut.Length);
            Assert.StartsWith(new string('x', 60) + "-", cut, StringComparison.Ordinal);
        }

        [Fact]
        public void Two_long_targets_that_share_sixty_characters_are_two_targets()
        {
            // Seventy characters each, identical for the first sixty. The id used to be the first sixty
            // characters and nothing else, so both landed on ONE entry: adding the second silently replaced
            // the first one's words, refiled it into the second one's section and cleared its done mark.
            var shared = new string('a', 60);
            var first = shared + new string('b', 10);
            var second = shared + new string('c', 10);

            Assert.NotEqual(PlanTargets.IdFor(first), PlanTargets.IdFor(second));
            // Still the same id for the same words on the next call, which is what a stored id relies on.
            Assert.Equal(PlanTargets.IdFor(first), PlanTargets.IdFor(first.ToUpperInvariant()));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Adding_two_long_targets_that_share_sixty_characters_keeps_both()
        {
            using var f = await GappyModelAsync("TargetLongIds.bim");
            var shared = new string('a', 60);
            var first = shared + new string('b', 10);
            var second = shared + new string('c', 10);

            await f.Engine.AddPlanTargetAsync(first, ModelPlanSections.Structure, "human");
            var plan = await f.Engine.AddPlanTargetAsync(second, ModelPlanSections.Meaning, "human");

            var mine = plan.Targets.Where(t => t.Source == ModelPlanTargetSources.You).ToArray();
            Assert.Equal(2, mine.Length);
            Assert.Contains(mine, t => t.Text == first);
            Assert.Contains(mine, t => t.Text == second);
            // The first one kept its own words AND its own section: a collision refiled it as well.
            Assert.Equal(ModelPlanSections.Structure, mine.Single(t => t.Text == first).Section);
            Assert.Equal(ModelPlanSections.Meaning, mine.Single(t => t.Text == second).Section);
            // And each one became its own item, so nothing was lost on the way to the page either.
            Assert.Equal(2, plan.Items.Count(i => i.Status == ModelPlanStatuses.Target));
        }

        // ---- both doors ----------------------------------------------------------------------------------

        [Fact]
        public void Both_doors_carry_the_target_operations_and_all_of_them_are_free()
        {
            var tools = typeof(McpTools).Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(m => m.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute"))
                .Where(a => a != null)
                .Select(a => (string?)a!.GetType().GetProperty("Name")?.GetValue(a))
                .ToHashSet(StringComparer.Ordinal);

            var rpc = typeof(EngineRpcTarget).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var op in new[] { "add_plan_target", "remove_plan_target" })
            {
                Assert.Contains(op, tools);
                Assert.True(FeatureMap.IsOperationClassified(op), op + " is not classified");
                Assert.Contains(op, FeatureMap.FreeOperations);
            }

            foreach (var method in new[] { "addPlanTarget", "removePlanTarget", "markPlanTargetDone" })
            {
                Assert.Contains(method, rpc);
                Assert.Contains(method, FeatureMap.FreeRpcMethods);
            }

            foreach (var method in new[] { "AddPlanTargetAsync", "RemovePlanTargetAsync", "MarkPlanTargetDoneAsync" })
            {
                Assert.NotNull(typeof(IEngine).GetMethod(method));
                Assert.Contains(method, FeatureMap.FreeEngineMethods);
            }
        }

        [Fact]
        public void Only_a_target_item_and_only_the_done_verb_take_the_target_branch()
        {
            // This predicate is the head of ModelPlanActionAsync's verb switch, which both doors reach
            // through one call each. It used to be forked at each door; it is one branch now.
            Assert.True(PlanTargets.IsTargetAction("target/every-measure-has-a-check", "done"));
            Assert.True(PlanTargets.IsTargetAction("target/every-measure-has-a-check", "DONE"));
            Assert.False(PlanTargets.IsTargetAction("target/every-measure-has-a-check", "start"));
            Assert.False(PlanTargets.IsTargetAction("safefix/Visibility", "done"));
            Assert.False(PlanTargets.IsTargetAction(null, "done"));
        }

        [Fact]
        public async Task The_done_verb_reaches_a_target_through_the_ordinary_plan_action()
        {
            using var f = await GappyModelAsync("TargetDoneVerb.bim");
            var added = await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");
            var item = Assert.Single(added.Items.Where(i => i.Status == ModelPlanStatuses.Target));
            var revision = f.Sessions.Current.Revision;

            // The verb a person and an assistant both send, through the one operation both doors call.
            var result = await f.Engine.ModelPlanActionAsync(item.Id, "done", "human");

            Assert.Equal(PlanTargets.DoneVerb, result.Action);
            Assert.False(result.Performed);
            Assert.Equal(revision, f.Sessions.Current.Revision);
            Assert.Contains("Nothing in the model changed", result.Sentence, StringComparison.Ordinal);
            Assert.Equal(ModelPlanStatuses.Done,
                Assert.Single(result.Plan.Items.Where(i => i.Id == item.Id)).Status);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_done_verb_is_refused_on_an_item_that_is_not_a_target()
        {
            using var f = await GappyModelAsync("TargetDoneWrongItem.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var ordinary = plan.Items.First(i => !PlanTargets.IsTargetItemId(i.Id));

            // "done" is a target's verb and nothing else's, so an ordinary item is refused by name rather
            // than quietly treated as one of the four.
            var refused = await Assert.ThrowsAsync<ArgumentException>(
                () => f.Engine.ModelPlanActionAsync(ordinary.Id, "done", "human"));

            foreach (var verb in new[] { "propose", "approve", "cancel", "start" })
                Assert.Contains("'" + verb + "'", refused.Message, StringComparison.Ordinal);
            Assert.Contains("answers 'done'", refused.Message, StringComparison.Ordinal);
        }

        // ---- the model must not move underneath a write (Astra 6 on the Fix slice) ----------------------

        [Fact]
        public async Task A_target_never_lands_in_the_model_that_was_opened_while_it_was_in_flight()
        {
            using var f = await GappyModelAsync("TargetRaceA.bim");
            var aPath = Path.Combine(f.Workspace, "TargetRaceA.bim");

            // A SECOND model in the SAME folder, so both resolve to one plan-memory.json and the only thing
            // keeping them apart is the identity the write is keyed by. That is exactly the seam under test.
            await f.Engine.CreateModelAsync("Contoso Two", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.SaveAsAsync("TargetRaceB.bim");
            var bPath = Path.Combine(f.Workspace, "TargetRaceB.bim");

            // Back to A, and this is the model the person is looking at when they type their target.
            await f.Engine.OpenAsync(aPath);
            f.UseNull();

            // Exchange the open model for B at the exact point a real caller is waiting on the plan gates.
            var swapped = false;
            f.Engine.PlanComposePauseForTest = async () =>
            {
                if (swapped) return;
                swapped = true;
                f.Engine.PlanComposePauseForTest = null;
                await f.Engine.OpenAsync(bPath);
                f.UseNull();
            };

            string refusal = null;
            try { await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human"); }
            catch (InvalidOperationException ex) { refusal = ex.Message; }
            finally { f.Engine.PlanComposePauseForTest = null; }

            Assert.True(swapped, "the seam never fired, so this test proved nothing");

            // B is the model that was open when the write happened, and B must not have the target.
            var onB = await f.Engine.GetModelPlanAsync();
            Assert.Empty(onB.Targets.Where(t => t.Source == ModelPlanTargetSources.You));
            Assert.Empty(onB.Items.Where(i => i.Status == ModelPlanStatuses.Target));

            // A may have it, or nothing may have been kept at all. Both are honest; landing on B is not.
            await f.Engine.OpenAsync(aPath);
            f.UseNull();
            var onA = await f.Engine.GetModelPlanAsync();
            var mine = onA.Targets.Where(t => t.Source == ModelPlanTargetSources.You).ToArray();
            if (refusal == null)
                Assert.Equal(CalendarTarget, Assert.Single(mine).Text);
            else
            {
                Assert.Empty(mine);
                Assert.Contains("different model is open now", refusal, StringComparison.Ordinal);
                Assert.DoesNotContain("\u2014", refusal, StringComparison.Ordinal);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_removal_that_races_a_model_swap_never_removes_the_other_model_s_target()
        {
            using var f = await GappyModelAsync("TargetRemoveRaceA.bim");
            var aPath = Path.Combine(f.Workspace, "TargetRemoveRaceA.bim");
            await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");

            // B gets the SAME words, so it gets the same target id. Two models, one folder, one sidecar
            // file, one id: if a write is not keyed by the right identity, it lands on the wrong model and
            // nothing about the data would tell you.
            await f.Engine.CreateModelAsync("Contoso Two", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.SaveAsAsync("TargetRemoveRaceB.bim");
            var bPath = Path.Combine(f.Workspace, "TargetRemoveRaceB.bim");
            await f.Engine.AddPlanTargetAsync(CalendarTarget, ModelPlanSections.Structure, "human");

            await f.Engine.OpenAsync(aPath);
            f.UseNull();

            var swapped = false;
            f.Engine.PlanComposePauseForTest = async () =>
            {
                if (swapped) return;
                swapped = true;
                f.Engine.PlanComposePauseForTest = null;
                await f.Engine.OpenAsync(bPath);
                f.UseNull();
            };

            try { await f.Engine.RemovePlanTargetAsync(PlanTargets.IdFor(CalendarTarget), "human"); }
            catch (InvalidOperationException) { /* refusing is the right answer; removing from B is not */ }
            finally { f.Engine.PlanComposePauseForTest = null; }

            Assert.True(swapped, "the seam never fired, so this test proved nothing");

            // B was the open model when the write happened. B never asked for anything to be removed.
            var onB = await f.Engine.GetModelPlanAsync();
            Assert.Equal(CalendarTarget,
                Assert.Single(onB.Targets.Where(t => t.Source == ModelPlanTargetSources.You)).Text);
        }

        [Fact]
        public void No_copy_on_this_surface_uses_an_em_dash()
        {
            var source = File.ReadAllText(Path.Combine(
                RepoRoot(), "Semanticus.Engine", "Plan", "PlanTargets.cs"));
            Assert.DoesNotContain("—", source, StringComparison.Ordinal);
        }
    }
}
