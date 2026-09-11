using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Sol's FOURTH unscoped read of the format v2 slice, against branch head 1008cbfe. Findings F-059 to
    /// F-064. One test per finding, each reproduced red before anything changed.
    ///
    /// Two of the six are in the GUARDS rather than in the parser, and those are the serious ones: a guard
    /// that can quietly stop proving anything is worse than a parser bug, because the parser bug is what the
    /// guard exists to catch. F-059 is the v1-forever golden turning into a mirror of the code it checks.
    /// </summary>
    public sealed class WorkflowV2SolUnscopedFourTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        // ---- F-059: the v1-forever golden writes itself when it is missing ---------------------------

        [Fact]
        public void F059_the_v1_golden_guard_never_writes_the_thing_it_compares_against()
        {
            // Measured, not argued: with workflow-v1-parse.txt deleted, run one FAILS but writes a golden
            // from today's parser, and run two PASSES against that mirror. The strongest guard on this branch
            // then proves that the parser agrees with itself. Deleting one file would have evaporated every
            // v1-forever proof while the suite stayed green.
            //
            // Asserted over the SOURCE because the hazard is a property of the guard, not of a parse: a
            // behavioural version would have to delete the repo's real golden, which is exactly the thing
            // nothing should ever do. The sibling shapes guard has always been fail-closed and is the shape
            // this one now copies.
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Tests", "WorkflowV1CompatibilityTests.cs"));
            Assert.DoesNotContain("File.WriteAllText", src);
            Assert.DoesNotContain("Directory.CreateDirectory", src);
        }

        [Fact]
        public void F059_a_missing_golden_throws_and_creates_nothing()
        {
            // The behavioural half, on a path in the temp directory so no repo file is ever at risk. One
            // helper decides what a missing golden means, for BOTH goldens, so the two cannot drift back
            // apart the way they had already drifted.
            var path = Path.Combine(Path.GetTempPath(), "smx-missing-golden-" + Guid.NewGuid().ToString("N") + ".txt");
            var ex = Record.Exception(() => WorkflowV1CompatibilityTests.RequireGolden("anything", path, "test golden"));
            Assert.NotNull(ex);
            Assert.False(File.Exists(path), "a missing golden was CAPTURED instead of refused: " + path);
            Assert.Contains("prev2-oracle", ex!.Message);
        }

        // ---- F-060: an explicitly empty inline-list item is deleted -----------------------------------

        [Theory]
        [InlineData("[\"\", Sales]")]     // Sol's input: an explicit, quoted empty item
        [InlineData("[Sales,, Product]")] // the same drop with nothing quoted at all
        public void F060_an_empty_inline_list_item_is_refused(string list)
        {
            // ParseInlineList ends with a Where(x => x.Length > 0), so an item the author WROTE is removed
            // from the result and nothing says so. A loop over a silently shortened list is the same class of
            // harm as a loop over a silently mangled one, which this helper already refuses two lines up.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach:", "  in: " + list, "  as: table", "```", "", "Body."));
            Assert.True(def.Error != null, $"forEach in {list} silently deleted an empty item");
        }

        [Fact]
        public void F060_control_a_list_with_no_empty_item_still_parses()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "tags: [finance, monthly]", "---",
                "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a legal list was refused: " + def.Error);
            Assert.Equal(new[] { "finance", "monthly" }, def.Tags);
        }

        [Fact]
        public void F060_control_v1_keeps_dropping_an_empty_item()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "tags: [\"\", finance]", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a v1 file with an empty list item was refused: " + def.Error);
            Assert.Equal(new[] { "finance" }, def.Tags);
        }

        // ---- F-061: an INDENTED step heading is dropped whole -----------------------------------------

        [Fact]
        public void F061_an_indented_step_heading_is_refused()
        {
            // Sol's input. The heading regex is anchored, so ' ## Step 1: Review' is not a heading: the step,
            // its body and anything in it are read as preamble prose and vanish. The same one-space authoring
            // mistake as the indented fence, one construct up, and the step numbering check cannot see it
            // either because as far as the parser is concerned that step was never written.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                " ## Step 1: Review", " Review the model.",
                "## Step 1: Deploy", "Deploy it."));
            Assert.True(def.Error != null, "an indented '## Step 1:' heading was dropped whole");
            Assert.Contains("Review", def.Error);
        }

        [Fact]
        public void F061_an_indented_step_heading_inside_a_step_body_is_refused_too()
        {
            // The same mistake one line later. Refused for the same reason the indented FENCE is refused
            // inside a body: the guard is about the construct being nearly right, not about where it sits.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Deploy", "Deploy it.", "  ## Step 2: Verify", "  Verify it."));
            Assert.True(def.Error != null, "an indented '## Step 2:' heading inside a body was dropped whole");
        }

        [Fact]
        public void F061_control_ordinary_prose_and_other_headings_stay_ordinary()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Deploy", "Deploy it.",
                "  ### Notes", "  Some indented notes.", "  Write '## Step' at the left margin."));
            Assert.True(def.Error == null, "ordinary indented prose was refused: " + def.Error);
        }

        // ---- F-062: a {{loop.*}} reference outside the step body ---------------------------------------

        [Theory]
        [InlineData("call:\n  workflow: other\n  with:\n    target: \"{{loop.table}}\"")]
        [InlineData("call:\n  workflow: other\n  returns: [\"{{loop.table}}\"]")]
        [InlineData("when: model.name == '{{loop.table}}'")]
        public void F062_a_slot_shaped_loop_reference_is_refused_wherever_it_is_written(string block)
        {
            // Section 1.6 gives the two delimiters two lifetimes: {{...}} is filled in ONCE at instantiation,
            // [[...]] is substituted fresh on every pass. A loop value wearing the slot delimiters is
            // indistinguishable in the file, which is why the spec requires EVERY use to be refused. Only
            // Instructions was scanned, so the refusal covered the step body and nothing else the step holds.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach:", "  in: [Sales]", "  as: table")
                + "\n" + block + "\n```\n\nBody.");
            Assert.True(def.Error != null, "a {{loop.*}} reference outside the step body was accepted");
            Assert.Contains("[[loop.table]]", def.Error);
        }

        [Fact]
        public void F062_control_the_loop_delimiters_are_still_accepted_everywhere()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach:", "  in: [Sales]", "  as: table",
                "call:", "  workflow: other", "  with:", "    target: \"[[loop.table]]\"", "```", "",
                "Body mentioning [[loop.table]] too."));
            Assert.True(def.Error == null, "a legal [[loop.*]] reference was refused: " + def.Error);
        }

        // ---- F-063: a step-scope root in a HAND-EDITED activation rule ---------------------------------

        [Fact]
        public async Task F063_a_step_scope_fact_in_a_hand_edited_activation_rule_is_linted()
        {
            // F-045 fixed exactly this in the BINDING reader and left the ACTIVATION reader open. The write
            // path (set_workflow_activation) refuses it, so the only way in is by hand-editing the settings
            // file, which is precisely the door F-045 was filed about. `inputs.approval.answered` parses
            // perfectly and has no value outside a run, so the rule never matches, the workflow it was meant
            // to switch off stays on, and nothing says a word.
            var ws = Path.Combine(Path.GetTempPath(), "smx-act4-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflow-settings.json"),
                "{\"activation\":[{\"workflow\":\"verified-measure\",\"when\":\"inputs.approval.answered\",\"set\":\"off\"}]}");

            var e = new LocalEngine(new SessionManager(), new FreeTier(), ws);
            var policy = await e.GetWorkflowPolicyAsync();
            Assert.Contains(policy.Lints, l => l.Message.Contains("while a workflow is running"));
        }

        [Fact]
        public async Task F063_control_a_run_independent_activation_condition_is_not_linted()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-act4c-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflow-settings.json"),
                "{\"activation\":[{\"workflow\":\"verified-measure\",\"when\":\"connection.isLive\",\"set\":\"off\"}]}");

            var e = new LocalEngine(new SessionManager(), new FreeTier(), ws);
            var policy = await e.GetWorkflowPolicyAsync();
            Assert.DoesNotContain(policy.Lints, l => l.Message.Contains("while a workflow is running"));
        }

        private sealed class FreeTier : Semanticus.Engine.Entitlement.IEntitlement
        {
            public bool IsPro => false;
            public Semanticus.Engine.Entitlement.EntitlementInfo Info =>
                new Semanticus.Engine.Entitlement.EntitlementInfo { Tier = "free" };
        }

        [Fact]
        public void F062_control_a_template_slot_reference_is_still_a_template_slot()
        {
            // {{plainSlot}} is a template fill-in and must keep working; only the loop-shaped one is refused.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "call:", "  workflow: other", "  with:",
                "    target: \"{{targetMeasure}}\"", "```", "", "Body."));
            Assert.True(def.Error == null, "a template slot reference was refused: " + def.Error);
        }
    }
}
