// Describing a workflow on the Workflows page is authoring (Kane's UAT on build ee57ba84, 25 Sep: "describe a workflow
// tries to add it to the plan to author and it becomes a mess"). The words become steps for Author, each matched to a
// step Semanticus knows or kept as a named placeholder, and nothing is put on the Plan.

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DescribedStepsTests
    {
        [Fact]
        public void Kanes_words_become_three_steps_and_the_when_is_not_a_step()
        {
            var d = DescribedSteps.From("Every month, refresh Sales, run the saved checks and publish the model.");
            Assert.Equal("Every month", d.When);
            Assert.Equal(new[] { "Refresh Sales", "Run the saved checks", "Publish the model" }, d.Steps.Select(s => s.Title));
            Assert.Equal(new[] { "refresh_partition", "run_tests", "deploy_stage" }, d.Steps.Select(s => s.Op));
            Assert.All(d.Steps, s => Assert.True(s.Matched));
        }

        [Fact]
        public void The_verb_that_leads_a_clause_wins_a_tie()
        {
            var d = DescribedSteps.From("Hide the key columns, then describe the Sales measures");
            Assert.Equal(new[] { "Hide the key columns", "Describe the Sales measures" }, d.Steps.Select(s => s.Title));
            Assert.Equal(new[] { "set_column_hidden", "set_description" }, d.Steps.Select(s => s.Op));
        }

        /// <summary>Verifier round 4, L1: "calculation" read as a slip of Add a measure's "calculate" though it starts Add a
        /// calculation group's own "calculation group"; a word another step names exactly is not a slip of this one, on the
        /// word match and on the Plan's menu alike.</summary>
        [Theory]
        [InlineData("build a time intelligence calculation group")]
        [InlineData("Please create a calculation group for time intelligence")]
        public void An_exact_keyword_wins_a_tie_over_a_slip_at_the_same_place(string words)
        {
            Assert.Equal("create_calculation_group", Assert.Single(DescribedSteps.From(words).Steps).Op);
            var menu = DraftMenu.For(words, null);
            Assert.Contains(menu, m => m.Matched && m.Id == "calc-group");
            Assert.DoesNotContain(menu, m => m.Matched && m.Id == "add-measure");
        }

        [Fact]
        public void A_clause_that_names_no_step_is_a_named_placeholder()
        {
            var d = DescribedSteps.From("Email the finance team; add a Total Returns measure");
            Assert.Equal("Email the finance team", d.Steps[0].Title);
            Assert.Null(d.Steps[0].Op);
            Assert.False(d.Steps[0].Matched);
            Assert.Equal("create_measure", d.Steps[1].Op);
            Assert.Contains("1 to fill in", d.Sentence);
        }

        [Fact]
        public void A_typo_still_names_its_step_and_performance_is_not_the_saved_checks()
        {
            // Kane's own words on the Yoga (25 Sep), typo included. The word match is the Free-of-Jev path, so it has to
            // survive a slip of one letter, and "test its performance" is a benchmark, not the saved checks.
            // On Workflows a read after the add is a step of the file (the assistant's), so nothing is unread after the add and it is kept
            // (M11 sol1-plan, join-2 item C; it was a placeholder from the join to then).
            var d = DescribedSteps.From("create a meaure and test its performance");
            Assert.Equal(new[] { "Create a meaure", "Test its performance" }, d.Steps.Select(s => s.Title));
            Assert.Equal(new[] { "create_measure", "benchmark_dax" }, d.Steps.Select(s => s.Op));
            Assert.All(d.Steps, s => Assert.Equal("words", s.ChosenBy));
            // The slip still names its step where the add is kept (nothing unread after it).
            var last = DescribedSteps.From("test its performance, then create a meaure");
            Assert.Equal(new[] { "benchmark_dax", "create_measure" }, last.Steps.Select(s => s.Op));
            Assert.All(last.Steps, s => Assert.Equal("words", s.ChosenBy));

            // One letter wrong in a longer word is still that word; a short word must match exactly.
            Assert.Equal("refresh_partition", DescribedSteps.From("refrsh the Sales table").Steps.Single().Op);
            Assert.Equal("set_column_hidden", DescribedSteps.From("hdie the keys").Steps.Single().Op);
            Assert.Null(DescribedSteps.From("Separate the blinking lights").Steps.Single().Op);
            Assert.Equal("run_tests", DescribedSteps.From("run the tests").Steps.Single().Op);
        }

        [Fact]
        public void A_keyword_inside_another_word_is_not_a_match()
        {
            // "rate" (Add a measure) inside "separate", "link" (Relate) inside "blinking": neither is a step.
            var d = DescribedSteps.From("Separate the blinking lights");
            Assert.Null(Assert.Single(d.Steps).Op);
        }

        [Fact]
        public async Task Describing_puts_nothing_on_the_Plan_and_is_on_both_doors()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Described.bim");
            var before = (await f.Engine.GetModelPlanAsync()).Items.Select(i => i.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();

            var ui = await f.Engine.DescribeWorkflowStepsAsync("Hide the key columns, then describe the Sales measures");
            var agent = await McpTools.DescribeWorkflowSteps(f.Engine, "Hide the key columns, then describe the Sales measures");
            Assert.Equal(ui.Steps.Select(s => s.Op), agent.Steps.Select(s => s.Op));

            var after = (await f.Engine.GetModelPlanAsync()).Items.Select(i => i.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(before, after);
            Assert.DoesNotContain(after, id => id.StartsWith("draft/", StringComparison.Ordinal));
        }

        // C7 (lane author-gaps, the patch for this file): a described step carries the checks its template carries, the
        // same as a compose_task draft does, and Author opens the description as a format 3 workflow so the checks and who
        // does each step are in the file, not only in the answer.
        [Fact]
        public async Task A_described_step_carries_its_templates_checks_and_opens_as_a_format_3_workflow()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("DescribedChecks.bim");
            // The add comes last here (the read first), the order this test has always used.
            var d = await f.Engine.DescribeWorkflowStepsAsync("test its performance, then create a meaure");

            Assert.Equal(DraftMenu.Find("add-measure").Checks.Select(c => c.Kind), d.Steps[1].Checks);
            Assert.Contains("valid_dax", d.Steps[1].Checks);
            Assert.Contains("bpa_clean", d.Steps[1].Checks);
            Assert.Empty(d.Steps[0].Checks);

            Assert.NotNull(d.Document);
            var def = WorkflowParser.Parse(d.Document.Text);
            Assert.Null(def.Error);
            Assert.Equal(3, def.SchemaVersion);
            Assert.Equal(2, def.Steps.Length);
            Assert.Equal(WorkflowPerformers.Semanticus, def.Steps[1].Performer);
            Assert.Equal("create_measure", def.Steps[1].Action.Op);
            Assert.Equal(new[] { "valid_dax", "dax_lint", "dax_probe", "bpa_clean" }, def.Steps[1].Checks.Select(c => c.Kind));
            // A read is not a change a draft can run in place, so the benchmark is your assistant's step, named in its brief.
            Assert.Equal(WorkflowPerformers.Assistant, def.Steps[0].Performer);
            Assert.Contains("benchmark_dax", def.Steps[0].Brief);
        }

        [Fact]
        public async Task A_placeholder_after_the_change_stays_after_it_in_the_file()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("DescribedOrder.bim");
            var d = await f.Engine.DescribeWorkflowStepsAsync("Every month, refresh Sales, run the saved checks and email finance.");
            var def = WorkflowParser.Parse(d.Document.Text);
            Assert.Null(def.Error);
            Assert.Equal(new[] { "Refresh Sales", "Run the saved checks", "Email finance" }, def.Steps.Select(s => s.Title));
            Assert.Equal(WorkflowPerformers.Assistant, def.Steps[2].Performer);
        }

        [Fact]
        public async Task Kanes_other_two_sentences_open_as_format_3_workflows()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("DescribedKane.bim");
            foreach (var words in new[] { "refresh the sales model and check its correct", "Every month, refresh Sales, run the saved checks and publish the model" })
            {
                var d = await f.Engine.DescribeWorkflowStepsAsync(words);
                Assert.True(d.Document != null, words + " could not be written as a workflow");
                var def = WorkflowParser.Parse(d.Document.Text);
                Assert.Null(def.Error);
                Assert.All(def.Steps, s => Assert.Equal(WorkflowPerformers.Semanticus, s.Performer));
            }
        }

        [Fact]
        public async Task Describing_is_Pro_like_the_rest_of_Workflows()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await Assert.ThrowsAnyAsync<Exception>(() => f.Engine.DescribeWorkflowStepsAsync("Hide the key columns"));
        }

        // ---- m11/completeness round 4 (verifier round 3, F1): a step that adds needs words that ask to add -------------

        /// <summary>Every way of asking to take something away that the verifier and the brief named in rounds 2 and 3: none
        /// asks to add, so none can be drafted as a step that adds, whatever the word match or Jev picks.</summary>
        [Theory]
        [InlineData("delete the replaced measures")]
        [InlineData("Deletion of the unused measures")]
        [InlineData("removal of the old measures")]
        [InlineData("delte the old measures")]
        [InlineData("remvoe the old measures")]
        [InlineData("Removing the unused columns")]
        [InlineData("drop the Sales Manager role")]
        [InlineData("get rid of the old measures")]
        [InlineData("clear out the unused measures")]
        [InlineData("clearing out the draft measures")]
        [InlineData("retire the old measures")]
        [InlineData("eliminate the duplicate measures")]
        [InlineData("scrap the draft measures")]
        [InlineData("throw away the draft measures")]
        [InlineData("throw the draft measures away")]
        [InlineData("trash the old measures")]
        [InlineData("take out the calculation group")]
        [InlineData("purge the stale measures")]
        [InlineData("erase the test measures")]
        [InlineData("discard the draft measures")]
        [InlineData("wipe the old measures")]
        [InlineData("unlink Sales and Date")]
        [InlineData("disconnect the Sales table from Date")]
        [InlineData("do away with the Sales Manager role")]
        [InlineData("please delete the replaced measures")]
        [InlineData("we need to remove the old measures")]
        [InlineData("go ahead and drop the old role")]
        [InlineData("I'd like to get rid of the old measures")]
        [InlineData("make sure to delete the replaced measures")]
        [InlineData("the replaced measures should be deleted")]
        [InlineData("the old measures need removing")]
        [InlineData("the old measures need to be removed")]
        [InlineData("have the old measures retired")]
        [InlineData("the Sales Manager role removal")]
        [InlineData("the unused measures taken out")]
        [InlineData("Get a role removed")]
        [InlineData("Take a measure out")]
        [InlineData("make a role go away")]
        [InlineData("Unconnecting the relationships")]
        [InlineData("Make a role disappear")]
        [InlineData("I want a role taken out of the model")]
        [InlineData("Cremate the old role")]
        [InlineData("the measures that were added last month should be removed")]
        [InlineData("the measures that have been added should be removed")]
        [InlineData("Create a workflow to delete the old role")]
        [InlineData("Total Sales measure should be deleted")]
        [InlineData("Bring the number of roles down to zero")]
        [InlineData("I need a new role removed")]
        [InlineData("Make another unused role disappear")]
        [InlineData("The old measures need a purge")]
        [InlineData("Create a workflow for deleting the old role")]
        [InlineData("Create a workflow that deletes the old role")]
        [InlineData("I want Total Sales measure deleted")]
        [InlineData("I need a new role removed tomorrow")]
        [InlineData("New measures that nobody uses need deleting")]
        [InlineData("Add no new roles")]
        [InlineData("Bring the number of unused roles to zero")]
        [InlineData("I need a new role removed right now")]
        [InlineData("Create a daily workflow that deletes unused roles")]
        [InlineData("Set up a workflow that deletes unused roles")]
        [InlineData("I need a new workflow that deletes unused roles")]
        [InlineData("I want Total Sales measure deleted immediately")]
        [InlineData("The Sales table needs a new measure removed")]
        [InlineData("The old role should be set for deletion")]
        [InlineData("Create a script that deletes unused roles")]
        [InlineData("New roles that need deleting")]
        // A second action after the add ("after deleting") makes the clause not only an add; the add is lost on the safe side.
        [InlineData("Add a measure after deleting the old role")]
        public void A_take_away_ask_does_not_ask_to_add(string words) =>
            Assert.False(DescribedSteps.AsksToAdd(words), "'" + words + "' asks to take something away, not to add");

        /// <summary>Words that ask to add: an adding verb in any form, spelled as it is, a light verb before a new thing
        /// (THE ADDING RULE version 2 counts "make a column"), a new thing named, and a thing that is to be made.</summary>
        [Theory]
        [InlineData("make a column for the order year")]
        [InlineData("make a new column for the order year")]
        [InlineData("relate Sales to Date")]
        [InlineData("link it to Calendar")]
        [InlineData("add a column for shipped orders")]
        [InlineData("add a role that can see Sales")]
        [InlineData("add a measure to Sales")]
        [InlineData("set up a role for auditors")]
        [InlineData("we need a new margin measure")]
        // Moved here at the M11 join from The_shared_rule_costs_these_adds: version 6 (D) reads "bring in" as an adding verb, and
        // an import may name a definite thing.
        [InlineData("Bring in the Targets sheet")]
        public void Words_that_ask_to_add_do(string words) =>
            Assert.True(DescribedSteps.AsksToAdd(words), "'" + words + "' asks to add");

        /// <summary>Re-labelled at the M11 join: these asked to add under the completeness lane's version 2, and the joined adding rule
        /// (version 6 with the join's amendments) keeps no add for them, the safe side. Each is friction, not harm: the person words it
        /// as a plain add ("add a measure for ..."), or ticks the add the Plan offers where the words hold an adding verb.</summary>
        [Theory]
        // v3a (K1): with no adding verb, a noun phrase never drafts an add; a bare "a new X" is not a verb.
        [InlineData("a new measure for average order value")]
        [InlineData("A new measure for shipped orders")]
        // K1: count, total, sum and average are not adding verbs.
        [InlineData("count the orders")]
        // K2: "the calculation group" is not a new thing (the Plan offers it unticked).
        [InlineData("please build the calculation group")]
        // K1: a participle never asks ("should be added").
        [InlineData("a Margin measure should be added")]
        public void The_joined_rule_keeps_no_add_for_these(string words) =>
            Assert.False(DescribedSteps.AsksToAdd(words), "'" + words + "' keeps an add under the joined rule; it was re-labelled as no add at the M11 join");

        /// <summary>THE ADDING RULE version 2 (the coordinator, 28 Sep 17:30) relabels these: a take-away sign anywhere in
        /// the clause is a veto, even where it only names a deleted, removed or dropped thing, and an action verb is never
        /// matched with a slip ("creat"). Each costs an add on the safe side; the step's reason says how to ask for one.</summary>
        [Theory]
        [InlineData("add a measure that counts deleted orders")]
        [InlineData("add a column that flags removed customers")]
        [InlineData("create a measure for dropped calls")]
        [InlineData("add a Dropped Calls measure")]
        [InlineData("total of the deleted orders")]
        [InlineData("creat a Profit measure")]
        public void Version_2_reads_no_add_in_a_take_away_sign_or_a_slip(string words) =>
            Assert.False(DescribedSteps.AsksToAdd(words), "'" + words + "' may not add under version 2");

        /// <summary>Adds the shared rule file (AddingRule.cs, m11/sol-fixes-1 7c840a24, carried byte for byte) does not read:
        /// "bring ... in" is not one of its adding verbs, and "needs a new measure" or "needs adding" said of a thing is not
        /// an ask it reads. Each costs an add on the safe side (a placeholder the person fills), reported to the coordinator
        /// rather than changed here.</summary>
        [Theory]
        [InlineData("the role needs adding")]
        [InlineData("the Sales table needs to have a new measure")]
        [InlineData("the Sales table needs a new measure")]
        [InlineData("bring the Budget table in from the source")]
        public void The_shared_rule_costs_these_adds(string words) =>
            Assert.False(DescribedSteps.AsksToAdd(words), "'" + words + "' is read as an add by the shared rule now; move it back to Words_that_ask_to_add_do");

        /// <summary>Words that ask for another action, with or without a removing word in them, do not ask to add; the
        /// step they ask for is not an add, so it stands.</summary>
        [Theory]
        [InlineData("hide the Deleted Date column")]
        [InlineData("rename the Removed Flag column to Is Removed")]
        [InlineData("describe the Deleted Orders measure")]
        [InlineData("Deleted Date should be hidden")]
        [InlineData("Deletion Flag should be hidden")]
        [InlineData("format the Removal Rate measure as a percent")]
        [InlineData("remove the format from the Margin measure")]
        [InlineData("merge the two date tables")]
        [InlineData("clean up the folders")]
        [InlineData("clear names for the measures")]
        [InlineData("refresh Sales")]
        [InlineData("make Quantity a whole number")]
        [InlineData("make the old measures go away")]
        [InlineData("a Margin measure should not be created")]
        [InlineData("invert the sort order")]
        [InlineData("confirm the KPI dictionary still holds")]
        public void Words_that_ask_for_another_action_do_not_ask_to_add(string words) =>
            Assert.False(DescribedSteps.AsksToAdd(words), "'" + words + "' does not ask to add");

        /// <summary>A clause the cut left with no action of its own takes the action of the clause before it, and a name
        /// followed by a determiner is a verb, not a name.</summary>
        [Theory]
        [InlineData("delete the old measures and the old role", "false,false")]
        [InlineData("remove the Sales and Date relationship", "false,false")]
        [InlineData("retire the old role and its measures", "false,false")]
        [InlineData("Delete: the old measures", "false,false")]
        // Version 2 read a clause with no action of its own as taking the action of the clause before it; the joined rule (version 6)
        // does not (v3a), and its whole-text belt unticks every kept add when a take-away sign is anywhere in the words (v6 B).
        // v3a: a noun phrase never drafts an add ("Returns measure" has no verb of its own; re-labelled at the M11 join).
        [InlineData("add a Units measure and Returns measure", "true,false")]
        // v3a: a noun phrase never drafts an add (re-labelled at the M11 join).
        [InlineData("add a Sales measure and the Margin measure", "true,false")]
        // K4: the take-away clause after the add has an action and no step, so the add is not kept (and v6 B, the whole-text belt,
        // would untick it anyway); re-labelled at the M11 join, as the rows marked K4 below.
        [InlineData("add a Sales measure, Delete the old role", "false,false")]
        // K4 ("dump the old ones"), and v6 B.
        [InlineData("add a Sales measure and dump the old ones", "false,false")]
        // K4 ("The old role should go", a later sentence), and v6 B.
        [InlineData("add a Sales measure. The old role should go", "false,false")]
        // v6 B: "remove" before the add unticks it too (re-labelled at the M11 join).
        [InlineData("remove the Region Manager role, then create a role for regional managers", "false,false")]
        // K4 ("Delete Sales role"), and v6 B (re-labelled at the M11 join, as the row below).
        [InlineData("Add a measure, Delete Sales role", "false,false")]
        [InlineData("Add a measure and Delete Sales role", "false,false")]
        [InlineData("Delete the measures and the role that has been added by mistake", "false,false")]
        // v6 B, the whole-text belt: "removal" (the clause after the add has no action, so K4 holds; re-labelled at the M11 join).
        [InlineData("I need a new measure and the removal of the old role", "false,false")]
        [InlineData("Delete the following: new measures created by mistake", "false,false")]
        [InlineData("The old role has been added by mistake, please delete it", "false,false")]
        [InlineData("Delete the following:\nnew measures\nnew roles", "false,false,false")]
        // The joined cut does not cut at "but" (one clause), and v6 B: "remove" (re-labelled at the M11 join).
        [InlineData("Add a measure but remove the old role", "false")]
        [InlineData("Add no new roles; delete the old ones", "false,false")]
        // K4 ("a purge of unused roles" holds a verb reading), and v6 B (re-labelled at the M11 join).
        [InlineData("I need a new measure and a purge of unused roles", "false,false")]
        // The shared rule does not read "needs a new measure" said of a thing as an ask (K1: offered unticked on the Plan): an add lost
        // on the safe side. The joined cut does not cut at "while": one clause (re-labelled at the M11 join).
        [InlineData("The model needs a new measure while removing the old role", "false")]
        [InlineData("Delete the Total Sales and Average Order Value measures", "false,false")]
        [InlineData("Delete these objects\n- new measures\n- new roles", "false,false,false")]
        // A trailing "added" is not read as an ask ("remove the Sales table and the role added last week" would add a role).
        [InlineData("I want a measure added and the old role removed", "false,false")]
        [InlineData("Delete the old measures and a role", "false,false")]
        // v3a: a noun phrase never drafts an add (re-labelled at the M11 join).
        [InlineData("add a Units measure and a Returns measure", "true,false")]
        public void A_clause_with_no_action_of_its_own_takes_the_one_before_it(string words, string asks) =>
            Assert.Equal(asks, string.Join(",", DescribedSteps.From(words).Steps.Select(s => s.AsksToAdd ? "true" : "false")));

        /// <summary>A named deleted thing keeps the step it asks for when that step does not add; under version 2 an add
        /// beside a take-away sign is vetoed, so those three draft no step that adds.</summary>
        [Theory]
        [InlineData("add a measure that counts deleted orders", null)]
        [InlineData("hide the Deleted Date column", "set_column_hidden")]
        [InlineData("add a Dropped Calls measure", null)]
        [InlineData("create a measure for dropped calls", null)]
        public void The_word_match_keeps_the_step_a_named_deleted_thing_asks_for(string words, string op)
        {
            var step = Assert.Single(DescribedSteps.From(words).Steps);
            if (op != null) Assert.Equal(op, step.Op);
            else Assert.DoesNotContain(step.Ops, o => o.StartsWith("create_", StringComparison.Ordinal));
        }

        [Fact]
        public void A_numbered_list_after_a_colon_goes_on_with_its_sentence() =>
            Assert.DoesNotContain(DescribedSteps.From("Delete the following:\n1. new measures\n2. new roles").Steps, x => x.AsksToAdd);

        [Fact]
        public void A_delete_in_a_described_workflow_stays_a_placeholder_with_its_reason_and_the_rest_are_steps()
        {
            // "counts late orders": under version 2, "counts deleted orders" carries a take-away sign and drafts no add.
            var d = DescribedSteps.From("get rid of the replaced measures, then add a measure that counts late orders, then hide the Deleted Date column");
            Assert.Equal(3, d.Steps.Length);
            Assert.Null(d.Steps[0].Op);
            Assert.Equal(DraftCopy.DescribeTakeAway, d.Steps[0].Reason);
            Assert.Equal("Get rid of the replaced measures", d.Steps[0].Title);
            // Re-labelled at the M11 join (v6 B, the whole-text belt): "get rid of" in the same words unticks the add, so on Workflows
            // it is a placeholder that says why; the hide stands.
            Assert.Null(d.Steps[1].Op);
            Assert.StartsWith(FirstSentence(AddingRule.SignReason), d.Steps[1].Reason);
            Assert.Equal("set_column_hidden", d.Steps[2].Op);
        }

        /// <summary>The Plan's door with Jev off is the menu, which shows only its matches when there are any: "delete the
        /// replaced measures" offered Add a measure alone. A step that adds is never a match for a delete, and the draft
        /// says the delete is not a step, on both doors.</summary>
        [Fact]
        public async Task The_Plan_menu_never_offers_an_add_for_a_delete_and_says_why_on_both_doors()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("PlanMenuDelete.bim");
            f.Engine.JevDraftsForTest = false;
            var studio = await new EngineRpcTarget(f.Engine).composeTask("clear out the replaced measures");
            var agent = await McpTools.ComposeTask(f.Engine, words: "eliminate the duplicate measures");
            foreach (var v in new[] { studio, agent })
            {
                Assert.Empty(v.Steps);
                Assert.DoesNotContain(v.Menu, m => m.Matched && m.Id is "add-measure" or "add-column" or "role" or "relate" or "calc-group" or "import" or "synonyms");
                // Re-labelled at the M11 join: with Jev off the joined Plan is the menu and says no reason (DraftCopy.PlanNotAnAdd is not
                // said); once Jev reads the words, the note for them gives the take-away reason (SolFixes1Round4Tests,
                // Jev_sure_of_an_add_for_words_that_take_away_drafts_no_add, and On_the_Plan_a_delete_beside_an_add below).
                Assert.Equal(DraftCopy.PickTheSteps, v.MenuLine);
            }
            // Words that ask to add still offer the add they ask for.
            var names = await McpTools.ComposeTask(f.Engine, words: "add a measure that counts late orders");
            Assert.Equal("add-measure", names.Menu.First(m => m.Matched).Id);
            Assert.DoesNotContain(DraftCopy.PlanNotAnAdd, names.MenuLine);
            // Version 2: a take-away sign vetoes the add even where it only names a deleted thing.
            var deleted = await McpTools.ComposeTask(f.Engine, words: "add a measure that counts deleted orders");
            Assert.DoesNotContain(deleted.Menu, m => m.Matched && m.Id == "add-measure");
        }

        /// <summary>Once Jev drafts on the Plan (the release's switch), a delete clause keeps no add Jev was sure of, the
        /// clause beside it that asks for an add keeps its own, Jev is not called unsure for the delete, and Add more words
        /// says the same.</summary>
        [Fact]
        public async Task On_the_Plan_a_delete_beside_an_add_keeps_only_the_add_it_was_asked_for()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("PlanDeleteBesideAdd.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            f.Engine.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new SureOfEveryAdd(), new Semanticus.Decisions.DecisionBudget()));

            var both = await McpTools.ComposeTask(f.Engine, words: "remove the replaced measures, then add a total sales measure");
            Assert.Single(both.Steps, s => s.Id == "add-measure");
            // Re-labelled at the M11 join: the joined Plan says why in the notes for the words (DraftCopy.PlanNotAnAdd is not said),
            // and its whole-text belt (v6 B) offers the add unticked, because "remove" is in the same words.
            Assert.False(both.Steps.Single(s => s.Id == "add-measure").Kept);
            Assert.Contains(both.Notes, n => n.StartsWith("No step for \"remove the replaced measures\"", StringComparison.Ordinal) && n.Contains(AddingRule.TakeAwayReason, StringComparison.Ordinal));

            var only = await new EngineRpcTarget(f.Engine).composeTask("retire the replaced measures");
            Assert.Empty(only.Steps);
            Assert.DoesNotContain(DraftCopy.JevNotSure, only.MenuLine);
            Assert.Contains(only.Notes, n => n.StartsWith("No step for \"retire the replaced measures\"", StringComparison.Ordinal) && n.Contains(AddingRule.TakeAwayReason, StringComparison.Ordinal));

            var more = await f.Engine.EditDraftAsync(only.DraftId, "refine", null, null, null, "and scrap the old role", "human");
            Assert.DoesNotContain(more.Steps, s => s.Id is "add-measure" or "role");
            Assert.DoesNotContain(DraftCopy.JevNotSure, more.Sentence);
            Assert.Contains(more.Notes, n => n.Contains("\"scrap the old role\"", StringComparison.Ordinal) && n.Contains(AddingRule.TakeAwayReason, StringComparison.Ordinal));
        }

        /// <summary>The Workflows door's wider reading of a step that adds (the M11 join, the coordinator's ruling of 30 Sep): Add synonyms
        /// makes nothing new, so it is this step's only when the words hold an adding verb. "add synonyms for Total Sales" keeps it, by the
        /// words and by a Jev sure of it; words that only take something away never get it, whatever Jev says (the completeness verifier,
        /// round 4: a Jev sure of Add synonyms drafted it for "clear the old measures"). Both doors.</summary>
        [Fact]
        public async Task On_Workflows_Add_synonyms_needs_an_adding_verb_even_when_Jev_is_sure_of_it()
        {
            Assert.Equal("set_synonyms", Assert.Single(DescribedSteps.From("add synonyms for Total Sales").Steps).Op);
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("WorkflowsSynonyms.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            f.Engine.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new SureOfTemplates("synonyms"), new Semanticus.Decisions.DecisionBudget()));
            foreach (var asked in new[] { await f.Engine.DescribeWorkflowStepsAsync("add synonyms for Total Sales"), await McpTools.DescribeWorkflowSteps(f.Engine, "add synonyms for Total Sales") })
            {
                var step = Assert.Single(asked.Steps);
                Assert.Equal(new[] { "set_synonyms" }, step.Ops);
                Assert.Equal("jev", step.ChosenBy);
            }
            // "make the old measures go away" and "Add a warning before you delete the old role" hold an adding verb, and take something away.
            foreach (var words in new[] { "clear the old measures", "make sure the old role is removed", "make the old measures go away", "Add a warning before you delete the old role" })
                foreach (var asked in new[] { await f.Engine.DescribeWorkflowStepsAsync(words), await McpTools.DescribeWorkflowSteps(f.Engine, words) })
                {
                    var step = Assert.Single(asked.Steps);
                    Assert.True(asked.JevAsked);
                    Assert.DoesNotContain("set_synonyms", step.Ops);
                    Assert.NotNull(step.Reason);
                }
        }

        /// <summary>The Plan door reads a step named Add that makes nothing new (Add synonyms) as the Workflows door does (the M11 join part 3,
        /// the coordinator's ruling on join-2 section 6 item A): a Jev sure of it is not let tick it when the words take something away
        /// ("clear the old measures"). "add synonyms for Total Sales" still gets it, ticked. Both doors: the MCP tool and the RPC method.</summary>
        [Fact]
        public async Task On_the_Plan_Add_synonyms_is_not_ticked_for_words_that_take_something_away_even_when_Jev_is_sure_of_it()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("PlanSynonyms.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            f.Engine.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new SureOfTemplates("synonyms"), new Semanticus.Decisions.DecisionBudget()));

            var asked = await McpTools.ComposeTask(f.Engine, words: "add synonyms for Total Sales");
            Assert.Contains(asked.Steps, s => s.Id == "synonyms" && s.Kept);

            foreach (var words in new[] { "clear the old measures", "make sure the old role is removed", "make the old measures go away" })
                foreach (var draft in new[] { await McpTools.ComposeTask(f.Engine, words: words), await new EngineRpcTarget(f.Engine).composeTask(words) })
                    Assert.DoesNotContain(draft.Steps, s => s.Id.StartsWith("synonyms", StringComparison.Ordinal) && s.Kept);
        }

        /// <summary>Jev sure (0.97) of the named templates, a sure no (0.02) to every other include, none to every choice.</summary>
        private sealed class SureOfTemplates : Semanticus.Decisions.IDecisionOracle
        {
            private readonly System.Collections.Generic.HashSet<string> _yes;
            public SureOfTemplates(params string[] ids) => _yes = ids.Select(id => "step:" + id + ":include").ToHashSet();
            public bool Available => true;
            public Task<Semanticus.Decisions.DecisionAnswers> AskAsync(Semanticus.Decisions.DecisionRequest request, System.Threading.CancellationToken ct)
            {
                if (request.PointName != Semanticus.Decisions.DecisionRegistry.ComposeDraft)
                    return Task.FromResult(Semanticus.Decisions.DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var sb = new System.Text.StringBuilder("{\"entries\":[{\"digest\":").Append(System.Text.Json.JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(System.Text.Json.JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(q.Name)).Append(':');
                    sb.Append(q.Kind == Semanticus.Decisions.DecisionQuestionKind.Noul
                        ? "{\"type\":\"noul\",\"noul\":" + (_yes.Contains(q.Name) ? "0.97" : "0.02") + "}"
                        : "{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new Semanticus.Decisions.RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }

        private static string FirstSentence(string text) => text.Substring(0, text.IndexOf(". ", StringComparison.Ordinal) + 1);

        /// <summary>Jev sure (0.97) of every template that adds, a sure no (0.02) to every other include, no pick.</summary>
        private sealed class SureOfEveryAdd : Semanticus.Decisions.IDecisionOracle
        {
            public bool Available => true;
            public Task<Semanticus.Decisions.DecisionAnswers> AskAsync(Semanticus.Decisions.DecisionRequest request, System.Threading.CancellationToken ct)
            {
                if (request.PointName != Semanticus.Decisions.DecisionRegistry.ComposeDraft)
                    return Task.FromResult(Semanticus.Decisions.DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var adds = DraftMenu.All.Where(DescribedSteps.Adds).Select(t => "step:" + t.Id + ":include").ToHashSet();
                var sb = new System.Text.StringBuilder("{\"entries\":[{\"digest\":").Append(System.Text.Json.JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(System.Text.Json.JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(System.Text.Json.JsonSerializer.Serialize(q.Name)).Append(':');
                    sb.Append(q.Kind == Semanticus.Decisions.DecisionQuestionKind.Noul
                        ? "{\"type\":\"noul\",\"noul\":" + (adds.Contains(q.Name) ? "0.97" : "0.02") + "}"
                        : "{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new Semanticus.Decisions.RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }
    }
}
