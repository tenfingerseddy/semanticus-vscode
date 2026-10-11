// THE ADDING RULE, version 3 (the coordinator, 29 Sep 00:55; it replaces the parts of version 2 it contradicts), on its own
// (Semanticus.Engine/Composer/AddingRule.cs): which clause may draft a step that adds, and the reason a clause that may not
// is kept with. Pure: no engine, no Jev. The doors, Jev on and off, are SolFixes1Round4Tests and SolFixes1Round5Tests. The
// version 2 cases are the completeness verifier's, labelled and committed before any run
// (tools/jev-replay/compose/adding-rule-v2-cases.json at 863d13f5), copied here as data; a label version 3 changes is
// marked "v3" with the part of the rule that changes it. Every such change loses an add (the safe side), none gains one.

// VERSION 5 RELABELS (round 7; each is listed in the report with the rule line that changes it). "v5 K4" rows: an add whose
// later words hold an action that got no step is UNTICKED, not kept (K4: "every later clause ... got a kept step, is a
// pleasantry, or only names model objects"), so the label true becomes false; the replace journey ("Add a measure for
// margin. Then delete the old measures.") is the coordinator's accepted call (29 Sep 19:05). "v5 K3": "to/into/onto/among
// before a determiner or pronoun" puts the thing into a set. "v5 harm": relabelled by the harm definition, as the
// coordinator ruled: a NEW thing is not the thing taken away.

using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine.Composer;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class AddingRuleTests
    {
        public static IEnumerable<object[]> VersionTwoCases() => new[]
        {
            new object[] { "v2-01", "make sure the old role is removed", new[] { false } },
            new object[] { "v2-02", "make sure the old measures are deleted", new[] { false } },
            new object[] { "v2-03", "make sure the old measures and the old role are deleted", new[] { false, false } },
            new object[] { "v2-04", "ensure the unused columns are removed", new[] { false } },
            new object[] { "v2-05", "check that the old role is gone", new[] { false } },
            new object[] { "v2-06", "see that the old measures get deleted", new[] { false } },
            new object[] { "v2-07", "make sure to add a Margin measure", new[] { true } },
            new object[] { "v2-08", "add a measure for margin and the old role should be removed", new[] { false, false } },   // v3 b
            new object[] { "v2-09", "Add a warning before you delete the old role", new[] { false } },
            new object[] { "v2-10", "Add a measure, then a purge of unused roles", new[] { false, false } },   // v3 b
            new object[] { "v2-11", "I need a new role removed within the hour", new[] { false } },
            new object[] { "v2-12", "New roles require deletion", new[] { false } },
            new object[] { "v2-13", "Set up removal of the old role", new[] { false } },
            new object[] { "v2-14", "Delete the total sales and average order value measures", new[] { false, false } },
            new object[] { "v2-15", "create a measure that excludes the old returns", new[] { false } },
            new object[] { "v2-16", "drop the unused columns and add a Units measure", new[] { false, false } },   // v6 B (relabelled from true): "drop" is a sign anywhere in the text, so the add is unticked
            new object[] { "v2-17", "refine the old measures", new[] { false } },
            new object[] { "v2-18", "creat a Profit measure", new[] { false } },
            new object[] { "v2-19", "improt the Budget table", new[] { false } },
            new object[] { "v2-20", "realte Sales to Date", new[] { false } },
            new object[] { "v2-21", "can you add a measure for margin", new[] { true } },
            new object[] { "v2-22", "now add a measure for margin", new[] { true } },
            new object[] { "v2-23", "let's create a role for managers", new[] { true } },
            new object[] { "v2-24", "we need a new measure for margin", new[] { true } },
            new object[] { "v2-25", "please create a role for the finance team", new[] { true } },
            new object[] { "v2-26", "let's make a measure for margin", new[] { true } },
            new object[] { "v2-27", "could you make a new column for the order year", new[] { true } },
            new object[] { "v2-28", "add a role for managers, the old role can go", new[] { false, false } },   // v3 b
            new object[] { "v2-29", "add a Units measure and the Returns measure has to go", new[] { false, false } },   // v3 b
            new object[] { "v2-vn-15", "add a measure that counts deleted orders", new[] { false } },
            new object[] { "v2-vn-20", "count the dropped calls", new[] { false } },
            new object[] { "v2-vn-21", "total the removed quantity", new[] { false } },
            new object[] { "v2-new-08", "make a column for the order year", new[] { true } },
            new object[] { "v2-new-24", "add a Units measure and Returns measure", new[] { true, false } },   // v3 a: no verb, no add
        };

        /// <summary>Which clause of the words the rule keeps ticked, as the Plan reads them: with each clause's sentence, and
        /// with the word match saying which clause got a step of its own (DescribedSteps.RuleOfWords; version 5).</summary>
        private static bool[] Kept(string words) => DescribedSteps.RuleOfWords(words).Select(r => r.AsksToAdd).ToArray();

        [Theory]
        [MemberData(nameof(VersionTwoCases))]
        public void The_completeness_verifiers_version_two_cases_read_as_labelled(string id, string words, bool[] asks)
        {
            var clauses = DescribedSteps.ClausesOf(words, out _);
            Assert.True(asks.Length == clauses.Count, id + ": cut into " + clauses.Count + " clauses, labelled " + asks.Length);
            Assert.Equal(asks, Kept(words));
        }

        /// <summary>MayAdd, one clause and the sentence it sits in, says the same as the whole reading. Version 5 retired the
        /// sentence-wide veto: a take-away BEFORE an add of a new thing no longer turns it down (K1 to K4 read the add's own
        /// words and what comes after it), so the row that asserted SentenceTakesAwayReason for "add a role for managers"
        /// after "the old role can go" now asserts a kept add (rule line: version 5 item 4, f retired as a gate).</summary>
        [Fact]
        public void May_add_reads_one_clause_with_the_clause_before_it()
        {
            // Version 3, a: a clause with no verb never adds, even after a clause that does.
            Assert.False(AddingRule.MayAdd("a role for managers", "create a measure for margin", out var reason));
            Assert.Equal(AddingRule.NoAddReason, reason);
            Assert.True(AddingRule.MayAdd("create a role for managers", "create a measure for margin", out reason));
            Assert.Null(reason);
            // Version 5: a take-away before the add is not read; the add is a NEW thing of a named kind.
            Assert.True(AddingRule.MayAdd("add a role for managers", "the old role can go", out _));
            Assert.False(AddingRule.MayAdd("the old role", "delete the old measures", out reason));
            Assert.Equal(AddingRule.TakeAwayReason, reason);
            Assert.False(AddingRule.MayAdd("refine the old measures", null, out reason));
            Assert.Equal(AddingRule.NoAddReason, reason);
            Assert.True(AddingRule.MayAdd("can you add a measure for margin", null, out _));
            // An add of something that may already exist is offered unticked, with the one true line on why.
            Assert.False(AddingRule.MayAdd("add the Managers role", null, out reason));
            Assert.Equal(AddingRule.NotNewReason, reason);
        }

        // ---- the rule on its own: which clause may draft a step that adds ------------------------------------------

        public static IEnumerable<object[]> Readings() => new[]
        {
            // The verifier's VR5: words that ask to take something away.
            new object[] { "make sure the old role is removed", new[] { false } },
            new object[] { "make sure the old measures are deleted", new[] { false } },
            new object[] { "make sure the old measures and the old role are deleted", new[] { false, false } },
            new object[] { "add a measure for margin and the old role should be removed", new[] { false, false } },   // v3 b
            new object[] { "refine the old measures", new[] { false } },
            // The verifier's VR6: polite and leading adds.
            new object[] { "can you add a measure for margin", new[] { true } },
            new object[] { "now add a measure for margin", new[] { true } },
            new object[] { "let's create a role for managers", new[] { true } },
            new object[] { "we need a new measure for margin", new[] { true } },
            // The rule's own examples and the verifier's same-root cases.
            new object[] { "please create a role for managers", new[] { true } },
            new object[] { "delete the old measures and the old role", new[] { false, false } },
            new object[] { "delete the old measures and a role", new[] { false, false } },
            new object[] { "create a measure for margin and a role for managers", new[] { true, false } },   // v3 a
            new object[] { "add a Units measure and a Returns measure", new[] { true, false } },   // v3 a
            new object[] { "create a measure for margin and create a role for managers", new[] { true, true } },
            new object[] { "new measures should be hidden", new[] { false } },
            new object[] { "count of orders should be hidden", new[] { false } },
            new object[] { "Deletion Flag should be hidden", new[] { false } },
            // Framing verbs: the action is what follows.
            new object[] { "make sure a margin measure is added", new[] { false } },   // v3 c: a participle asks nothing
            new object[] { "make sure to add a measure for margin", new[] { true } },
            new object[] { "check that the old role is deleted", new[] { false } },
            new object[] { "ensure the old role is gone", new[] { false } },
            // Take-away signs, wherever they sit: a veto.
            new object[] { "the old measures have to go", new[] { false } },
            new object[] { "we no longer need the old role", new[] { false } },
            new object[] { "get rid of the unused columns", new[] { false } },
            new object[] { "clean up the old measures", new[] { false } },
            new object[] { "retire the Legacy role", new[] { false } },
            new object[] { "add a measure that is not needed", new[] { false } },
            new object[] { "add a measure for margin, then take the old role out", new[] { false, false } },   // v3 b
            new object[] { "create a role and remove the old one", new[] { false, false } },   // v3 b
            new object[] { "make the old role go away", new[] { false } },
            // A thing named with nothing said of it never adds, whatever leads it (v3 a).
            new object[] { "a new role for managers", new[] { false } },
            new object[] { "total sales by region", new[] { false } },
            new object[] { "hide Amount, then also Qty please", new[] { false, false } },
            // "make" on a thing already there changes it; no fuzzy match turns a slip into an add.
            new object[] { "make the keys hidden", new[] { false } },
            new object[] { "craete a measure for margin", new[] { false } },
        };

        [Theory]
        [MemberData(nameof(Readings))]
        public void The_adding_rule_lets_a_clause_add_only_when_it_asks_to_and_nothing_takes_away(string words, bool[] asks)
        {
            Assert.Equal(asks, Kept(words));
        }

        /// <summary>The reason a clause with no step gives is true of it: a take-away sign in it, in the clause it takes its
        /// action from, or, for a clause whose action is still to come, in the clause after it. Version 5: an add the rule does
        /// not keep because words after it are unread is UNTICKED with the unread line, not left with no step.</summary>
        [Fact]
        public void A_clause_the_rule_turns_down_gives_the_reason_that_is_true_of_it()
        {
            Assert.All(DescribedSteps.RuleOfWords("make sure the old measures and the old role are deleted"),
                r => Assert.Equal(AddingRule.TakeAwayReason, AddingRule.ReasonFor(r)));
            Assert.All(DescribedSteps.RuleOfWords("delete the old measures and the old role"),
                r => Assert.Equal(AddingRule.TakeAwayReason, AddingRule.ReasonFor(r)));
            Assert.Equal(AddingRule.NoAddReason, AddingRule.ReasonFor(DescribedSteps.RuleOfWords("refine the old measures")[0]));
            // An add whose later words are not read is unticked with that line; the take-away itself keeps its own reason.
            var mixed = DescribedSteps.RuleOfWords("add a measure for margin and the old role should be removed");
            Assert.Equal(AddingRule.Tier.Unticked, mixed[0].Tier);
            Assert.Equal(AddingRule.UnreadReason, mixed[0].Reason);
            Assert.Equal(AddingRule.TakeAwayReason, AddingRule.ReasonFor(mixed[1]));
            // A clause that only names a thing, whose take-away is in the next sentence, says so.
            Assert.All(DescribedSteps.RuleOfWords("The new Margin measure. Delete it."), r => Assert.Equal(AddingRule.TakeAwayReason, AddingRule.ReasonFor(r)));
        }

        // ---- version 3 (the coordinator, 29 Sep 00:55), after the verifier's VR8 -------------------------------------

        /// <summary>Version 3's own cases, each part: a) only an explicit adding verb adds, a noun phrase never; b) the
        /// veto covers the whole sentence and only it; c) participles and past forms ask nothing; d) the new signs. Read
        /// with each clause's sentence, as the Plan reads them.</summary>
        public static IEnumerable<object[]> VersionThree() => new[]
        {
            // a. A noun phrase with no verb never adds, whatever word leads it.
            new object[] { "the new role and the new measure should be deleted", new[] { false, false } },
            new object[] { "Total Sales and Average Price should be removed", new[] { false, false } },
            new object[] { "Total Sales measure, delete it", new[] { false, false } },
            new object[] { "The new Margin measure. Delete it.", new[] { false, false } },
            new object[] { "new measures and new roles have to go", new[] { false, false } },
            new object[] { "count of orders and count of returns should be hidden", new[] { false, false } },
            new object[] { "a new measure for margin", new[] { false } },
            new object[] { "count the orders", new[] { false } },
            new object[] { "make old roles vanish", new[] { false } },
            new object[] { "build the model without the old role", new[] { false } },
            new object[] { "give the Sales table a new measure for margin", new[] { true } },
            new object[] { "introduce a role for managers", new[] { true } },
            new object[] { "set up a role for managers", new[] { true } },
            new object[] { "I'd like a new measure for margin", new[] { true } },
            new object[] { "remember to create a role for managers", new[] { true } },
            new object[] { "could you please add a measure for margin", new[] { true } },
            new object[] { "Add a measure for margin please", new[] { true } },
            new object[] { "don't add a role", new[] { false } },
            new object[] { "undo the new measure", new[] { false } },
            // b. The veto covers the sentence, before and after the clause, and ends with it.
            new object[] { "add a Units measure. Remove the old role.", new[] { false, false } },   // v5 K4
            new object[] { "Remove the old role. Add a Units measure.", new[] { false, false } },   // v6 B (relabelled from true): "Remove" anywhere in the text demotes the add
            new object[] { "add a Units measure\nremove the old role", new[] { false, false } },   // v5 K4
            new object[] { "add a measure worth 1.5 times Sales, then delete the old one", new[] { false, false, false } },
            new object[] { "add a Units measure\nthe old role has to go", new[] { false, false } },   // v5 K4 (v4 f: a line break ends a sentence, and so does ";")
            // c. Participles and past forms describe a thing.
            new object[] { "the measure that was added yesterday, remove it", new[] { false, false } },
            new object[] { "the columns that were added by the import, drop them", new[] { false, false } },
            new object[] { "a margin measure should be added", new[] { false } },
            new object[] { "the role created yesterday", new[] { false } },
            new object[] { "the new measures we built", new[] { false } },
            new object[] { "added a role for managers", new[] { false } },
            // d. The new take-away signs.
            new object[] { "create a cleanup step for the old roles", new[] { false } },
            new object[] { "create a clean-up step for the old roles", new[] { false } },
            new object[] { "set up a cleanup of the old measures", new[] { false } },
            new object[] { "add a step to purge the old roles", new[] { false } },
            new object[] { "add a measure and prune the old ones", new[] { false, false } },
            new object[] { "create a role to trim the list", new[] { false } },
            new object[] { "add a measure before we decommission the old model", new[] { false } },
            new object[] { "create a role, we sunset the old one", new[] { false, false } },
            new object[] { "add a measure and archive the old ones", new[] { false, false } },
        };

        [Theory]
        [MemberData(nameof(VersionThree))]
        public void Version_three_reads_each_clause_with_its_sentence(string words, bool[] asks)
        {
            var clauses = DescribedSteps.ClausesOf(words, out _, out var sentences);
            Assert.True(asks.Length == clauses.Count, words + ": cut into " + clauses.Count + " clauses: " + string.Join(" | ", clauses));
            Assert.Equal(asks, Kept(words));
        }

        // ---- version 4 (the coordinator, 29 Sep 05:10), after the verifier's VR9 to VR12 -----------------------------

        /// <summary>Version 4, one part each: f) a take-away by PRESENCE over the whole sentence, at any distance and across
        /// "and" and commas; g) an adding verb whose object is a job, task, step, list, note, reminder, plan, check or ticket;
        /// h) the wanting form adds only before "new"; i) a negated or zero add; k) a later sentence that takes away the same
        /// kind of thing; l) "give ... a new name" is an edit. Read with each clause's sentence, as the Plan reads them.</summary>
        public static IEnumerable<object[]> VersionFour() => new[]
        {
            // f. The verifier's nine phrasings, each read as one sentence with the sign anywhere in it.
            new object[] { "f", "create a job that cleans the old roles up", new[] { false } },
            new object[] { "f", "set up a task to clean all the old measures out", new[] { false } },
            new object[] { "f", "build a new step that throws every one of the old roles away", new[] { false } },
            new object[] { "f", "add a step that takes all of the old roles out of the model", new[] { false } },
            new object[] { "f", "create a task to take Total Sales and Total Cost out", new[] { false, false } },
            new object[] { "f", "set up a step to throw the old roles and the old measures away", new[] { false, false } },
            new object[] { "f", "set up a job for the old roles, they should all go", new[] { false, false } },
            new object[] { "f", "add the Managers role to the roles that aren't needed", new[] { false } },
            new object[] { "f", "add Total Sales to the measures we no longer really need", new[] { false } },
            // f. The signs the rule lists, read over a sentence whose first clause asks to add.
            new object[] { "f", "add a role for managers and get every one of the old roles out", new[] { false, false } },
            new object[] { "f", "add a measure for margin and eliminate the old ones", new[] { false, false } },
            new object[] { "f", "add a measure for margin and discard the old ones", new[] { false, false } },
            new object[] { "f", "add a measure for margin and scrap the old ones", new[] { false, false } },
            new object[] { "f", "add a measure for margin and ditch the old ones", new[] { false, false } },
            new object[] { "f", "add a measure for margin, the old ones are unneeded", new[] { false, false } },
            new object[] { "f", "add a measure for margin, the old ones are unnecessary", new[] { false, false } },
            new object[] { "f", "add a measure for margin, the old ones are redundant", new[] { false, false } },
            new object[] { "f", "add a measure for margin and we don't need the old one", new[] { false, false } },
            new object[] { "f", "add a measure for margin and the old one doesn't need to stay", new[] { false, false } },
            new object[] { "f", "add a measure for margin and the old ones aren't needed", new[] { false, false } },
            new object[] { "f", "add a measure for margin and the old one isn't needed", new[] { false, false } },
            new object[] { "f", "add a measure for margin and we no longer really need the old ones", new[] { false, false } },
            new object[] { "f", "add a role for managers and the old roles should all go", new[] { false, false } },
            new object[] { "f", "add a role for managers and the old roles must go", new[] { false, false } },
            new object[] { "f", "add a role for managers and the old role needs to go", new[] { false, false } },
            new object[] { "f", "add a role for managers and the old roles are gone", new[] { false, false } },
            new object[] { "f", "add a role for managers and throw the old one right away", new[] { false, false } },
            new object[] { "f", "add a role for managers and clear the old ones off the list", new[] { false, false } },
            new object[] { "f", "add a role for managers and take the old ones off", new[] { false, false } },
            new object[] { "f", "add a measure for margin and take Total Sales and Total Cost out", new[] { false, false, false } },
            new object[] { "f", "add a measure for margin with less noise", new[] { false } },
            new object[] { "f", "add a measure for margin with fewer columns", new[] { false } },
            new object[] { "f", "add a measure and cut the old ones", new[] { false, false } },
            new object[] { "f", "add a measure and strip the old ones", new[] { false, false } },
            new object[] { "f", "add a role for managers and the old roles are to be cleaned", new[] { false, false } },
            new object[] { "f", "add a measure for margin, then clean it later on", new[] { false, false } },
            // f. Controls: words near a sign that are not one keep their add.
            new object[] { "f", "please go ahead and add a measure for margin", new[] { false, true } },
            new object[] { "f", "can you go ahead and add a role for managers", new[] { false, true } },
            new object[] { "f", "add a role for managers and go on to the measures", new[] { false, false } },   // v5 K4: "go on to the measures" is an action with no step
            new object[] { "f", "add a measure for margin and take a look at it", new[] { false, false } },   // v5 K4: "take a look at it" is an action with no step
            new object[] { "f", "add a role for managers and hide the Margin measure", new[] { true, false } },
            // f. Sentence ends are . ! ? ; and a line break; "e.g." and "i.e." never end one.
            new object[] { "f", "add a Units measure; the old role has to go", new[] { false, false } },   // v5 K4: a later sentence with an action and no step
            new object[] { "f", "add a measure for margin, e.g. a ratio, and the old role can go", new[] { false, false, false } },
            new object[] { "f", "create a step for the old roles, e.g. deleting them", new[] { false, false } },
            new object[] { "f", "add a role for managers, i.e. the finance people. The old role can go.", new[] { false, false, false } },
            // g. An adding verb whose object is a job, task, step, list, note, reminder, plan, check or ticket asks for work.
            new object[] { "g", "create a job for the old roles", new[] { false } },
            new object[] { "g", "add a step for margin", new[] { false } },
            new object[] { "g", "create a list of the Sales measures", new[] { false } },
            new object[] { "g", "add a note about the margin measure", new[] { false } },
            new object[] { "g", "set up a reminder for the roles", new[] { false } },
            new object[] { "g", "make a plan for the new measures", new[] { false } },
            new object[] { "g", "add a check for the Sales table", new[] { false } },
            new object[] { "g", "create a ticket for a new role", new[] { false } },
            new object[] { "g", "build a new task that adds a role", new[] { false } },
            new object[] { "g", "make me a list of measures", new[] { false } },
            new object[] { "g", "add a measure for the job costs", new[] { true } },
            new object[] { "g", "add a job cost measure", new[] { true } },
            new object[] { "g", "add a measure to the task list", new[] { false } },   // v5 K3: "to the task list" puts it into a list
            new object[] { "g", "add a measure that lists the tasks", new[] { true } },
            // h. Wanting: "new" must come before the thing. "one less", "two fewer" and "some more of" never add.
            new object[] { "h", "we need a new role for managers", new[] { true } },
            new object[] { "h", "I want a new column for the order year", new[] { true } },
            new object[] { "h", "I'd like a brand new measure for margin", new[] { true } },
            new object[] { "h", "I think we need a new measure for margin", new[] { true } },
            new object[] { "h", "I would like you to add a measure for margin", new[] { true } },
            new object[] { "h", "we need one less role", new[] { false } },
            new object[] { "h", "I'd like two fewer measures", new[] { false } },
            new object[] { "h", "we need some more of them", new[] { false } },
            new object[] { "h", "we need a role for managers", new[] { true } },   // v6 D (relabelled from false): a bare want of a new thing is an ask ("we need a X")
            new object[] { "h", "we want another measure", new[] { true } },   // v6 D (relabelled from false): "another" marks it new
            new object[] { "h", "add one less role", new[] { false } },
            new object[] { "h", "add two fewer measures", new[] { false } },
            new object[] { "h", "add some more of the measures", new[] { false } },
            // i. Negated or zero.
            new object[] { "i", "add no new roles", new[] { false } },
            new object[] { "i", "create zero new measures", new[] { false } },
            new object[] { "i", "create zero measures", new[] { false } },
            new object[] { "i", "add none of the roles", new[] { false } },
            new object[] { "i", "don't add a role", new[] { false } },
            new object[] { "i", "never create a measure for margin", new[] { false } },
            new object[] { "i", "we do not need to add a role", new[] { false } },
            new object[] { "i", "there is no need to add a measure", new[] { false } },
            new object[] { "i", "you can't add a role", new[] { false } },
            new object[] { "i", "let's not add a role for managers", new[] { false } },
            new object[] { "i", "please do not create a measure for margin", new[] { false } },
            new object[] { "i", "add a measure with no filter", new[] { true } },
            new object[] { "i", "add a measure for margin and a role for managers", new[] { true, false } },
            // k. A later sentence that takes away the same kind of thing (or one that names no kind) vetoes the add.
            new object[] { "k", "Add a measure for margin. Then delete the old measures.", new[] { false, false } },
            new object[] { "k", "Add a role for managers. Then delete the old ones.", new[] { false, false } },
            new object[] { "k", "Add a measure for margin. Then remove the old role.", new[] { false, false } },   // v5 K4: the later sentence holds an action with no step
            new object[] { "k", "Add a measure for margin. Delete the old roles and columns.", new[] { false, false, false } },   // v5 K4
            new object[] { "k", "Add a Units measure. Add a role for managers. Delete the old measures.", new[] { false, false, false } },   // v5 K4: both adds come before the delete, so neither is kept
            new object[] { "k", "Create a list of the old measures. Then delete everything on it.", new[] { false, false } },
            new object[] { "k", "Add a role for managers? No. Remove the old one instead.", new[] { false, false, false } },   // v5: a question mark ends a clause, so "No" is a clause of its own
            new object[] { "k", "Remove the old role. Add a measure for margin.", new[] { false, false } },   // v6 B (relabelled from true): "Remove" anywhere demotes the add
            // l. "give <thing> a new name, description, format, folder, display folder or label" is an edit.
            new object[] { "l", "give the old role a new name", new[] { false } },
            new object[] { "l", "give Total Sales a new description", new[] { false } },
            new object[] { "l", "give the Sales table a new format", new[] { false } },
            new object[] { "l", "give the measure a new display folder", new[] { false } },
            new object[] { "l", "give the column a new folder", new[] { false } },
            new object[] { "l", "give it a new label", new[] { false } },
            new object[] { "l", "give the Sales table a new measure for margin", new[] { true } },
        };

        [Theory]
        [MemberData(nameof(VersionFour))]
        public void Version_four_reads_each_clause_by_presence_over_its_sentence(string letter, string words, bool[] asks)
        {
            var clauses = DescribedSteps.ClausesOf(words, out _, out var sentences);
            Assert.True(asks.Length == clauses.Count, letter + ": " + words + ": cut into " + clauses.Count + " clauses: " + string.Join(" | ", clauses));
            Assert.Equal(asks, Kept(words));
        }

        /// <summary>L2: the reason kept with a clause is true of it. A clause that asks for a job says "take away" only when
        /// its sentence does, and says "do not ask to add" otherwise; the clause that takes away says "take away"; an add held back by what comes after it is unticked with the unread line; the "g" of
        /// "e.g." is never a clause of its own. (Version 5 retired SentenceTakesAwayReason and LaterTakesAwayReason, whose
        /// advice did not work: rule line, version 5 item 4.)</summary>
        [Fact]
        public void Version_four_reasons_are_true_of_each_clause()
        {
            var taking = DescribedSteps.RuleOfWords("create a task to take Total Sales and Total Cost out");
            Assert.Equal(AddingRule.TakeAwayReason, AddingRule.ReasonFor(taking[0]));   // a task for work, in a sentence that takes away
            Assert.Equal(AddingRule.TakeAwayReason, AddingRule.ReasonFor(taking[1]));
            var clauses = DescribedSteps.ClausesOf("Create a step for the old roles, e.g. deleting them", out _, out _);
            Assert.DoesNotContain(clauses, c => c == "g" || c == "e");
            var eg = DescribedSteps.RuleOfWords("Create a step for the old roles, e.g. deleting them");
            Assert.Equal(AddingRule.TakeAwayReason, AddingRule.ReasonFor(eg[0]));
            Assert.Equal(AddingRule.TakeAwayReason, AddingRule.ReasonFor(eg[1]));
            var later = DescribedSteps.RuleOfWords("Add a measure for margin. Then delete the old measures.");
            Assert.Equal(AddingRule.Tier.Unticked, later[0].Tier);
            Assert.Equal(AddingRule.UnreadReason, later[0].Reason);
            Assert.Equal(AddingRule.TakeAwayReason, AddingRule.ReasonFor(later[1]));
            // A job that asks for no take-away keeps the honest "does not ask to add" reason.
            Assert.Equal(AddingRule.NoAddReason, AddingRule.ReasonFor(DescribedSteps.RuleOfWords("create a job for the old roles")[0]));
        }

        /// <summary>The verifier's round 5 rows on the rule alone, with the sentence the Plan cuts (VR9 and VR10, and Q1).</summary>
        [Theory]
        [InlineData("Add a role for managers? No. Remove the old one instead.")]
        [InlineData("Create a step for the old roles, e.g. deleting them")]
        [InlineData("we need one less role")]
        [InlineData("I'd like two fewer measures")]
        [InlineData("give the old role a new name")]
        [InlineData("add no new roles")]
        public void No_clause_of_the_verifiers_round_five_words_asks_to_add(string words)
        {
            var clauses = DescribedSteps.ClausesOf(words, out _, out var sentences);
            Assert.All(DescribedSteps.RuleOfWords(words), r => Assert.False(r.AsksToAdd));
        }

        /// <summary>Sentences end at ! ? a line break and a full stop before a space; a clause whose words run over one
        /// joins the sentences it touches (the safe side).</summary>
        [Fact]
        public void A_sentence_ends_where_the_words_end_one()
        {
            Assert.Equal(new[] { 0, 1 }, Sentence("add a Units measure. Remove the old role."));
            Assert.Equal(new[] { 0, 0 }, Sentence("add a Units measure, remove the old role"));
            Assert.Equal(new[] { 0, 0, 0 }, Sentence("add a measure worth 1.5 times Sales, then delete the old one"));
            // Version 5: a question mark ends a clause too ("Add a role? No." puts "No" in a later clause).
            Assert.Equal(new[] { 0, 1, 1 }, Sentence("Total Sales? delete it, then add a role"));
            Assert.Null(DescribedSteps.OpenSentence("delete the old measures."));
            Assert.Equal("delete the old measures", DescribedSteps.OpenSentence("hide the keys. delete the old measures"));
            Assert.Equal("delete the old measures", DescribedSteps.OpenSentence("delete the old measures"));
        }

        private static int[] Sentence(string words)
        {
            DescribedSteps.ClausesOf(words, out _, out var sentences);
            return sentences;
        }
    }
}
