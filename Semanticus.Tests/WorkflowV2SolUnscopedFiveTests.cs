using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Sol's FIFTH unscoped read of the format v2 slice, against branch head 140d8fd7. Findings F-065 to
    /// F-070. Each reproduced red before anything changed.
    ///
    /// TWO OF THE SIX WERE CLASS A: they changed how a frozen V1 file parses, which is the load-bearing
    /// claim of the whole slice. Those two (F-065, F-066) are NOT tested here. They are pinned end-to-end
    /// against the PINNED pre-v2 parser through fixtures/workflow-v1-shapes/ and the golden, because a test
    /// written from this branch's reading of v1 is the branch grading its own homework. See
    /// WorkflowV2SolRoundTwoTests.U5_* for the assertions and the README table for the fixture rows.
    ///
    /// Both were introduced by fixes made for v2 in earlier rounds, and both leaked into v1 because the fix
    /// went into a reader that BOTH rulesets share. PR #301's residual risk 6 predicted this in advance: the
    /// v1 golden proves only the shapes the corpus contains. It contained neither.
    /// </summary>
    public sealed class WorkflowV2SolUnscopedFiveTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        // ---- F-069 and F-070: a comment and a spec sentence that taught a false invariant -------------

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + System.AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static string RepoText(string rel) =>
            System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        [Fact]
        public void F069_controls_are_retained_and_call_execution_is_wired_at_start()
        {
            // The original defect was a comment saying controls were ignored. Public calls now execute;
            // the start initializer and the three-door integration tests replace the old blanket refusal.
            var wf = RepoText("Semanticus.Engine/Workflow.cs");
            Assert.DoesNotContain("still runs linearly", wf, System.StringComparison.Ordinal);
            Assert.Contains("WorkflowRunner.InitializeCalls",
                RepoText("Semanticus.Engine/LocalEngine.Workflows.cs"), System.StringComparison.Ordinal);
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: controls", "title: Controls", "---",
                "## Step 1: Delegate each", "Body.", "```yaml step", "id: each",
                "forEach:", "  in: [North]", "  as: region", "call:", "  workflow: worker", "```"));
            Assert.Null(def.Error);
            Assert.Equal("worker", def.Steps[0].Call.Workflow);
            Assert.Equal(new[] { "North" }, def.Steps[0].ForEach.InLiteral);
            Assert.Empty(WorkflowParser.UnexecutableControlFields(def));

            var unknown = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: unknown-control", "title: Unknown", "---",
                "## Step 1: Delegate", "Body.", "```yaml step", "id: step", "inventedControl: ignored", "```"));
            Assert.NotNull(unknown.Error);
            Assert.Contains("inventedControl", unknown.Error, System.StringComparison.Ordinal);
        }

        [Fact]
        public void F070_the_spec_states_both_open_maps_not_one()
        {
            // Also first filed as `NO TEST:`, on the reasoning that a spec sentence is untestable. It is
            // not: a document in the repo is a file, and the F-075 precedent one read later proves the
            // class. Taking the frozen NO_TEST_ALLOWED hatch here would have spent a deliberately scarce
            // exemption on a row that simply needed the test writing.
            var spec = RepoText("docs/workflow-canvas-spec.md");
            Assert.DoesNotContain("`with:` is the one open map", spec, System.StringComparison.Ordinal);
            Assert.Contains("two** open maps", spec, System.StringComparison.Ordinal);
            Assert.Contains("provenance:", spec, System.StringComparison.Ordinal);

            // The behaviour the corrected sentence describes, so the document is checked against the parser
            // and not only against itself: provenance really does take a key nothing declares.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "provenance:", "  anything: value", "---",
                "## Step 1: Do", "Body."));
            Assert.Null(def.Error);
            Assert.Equal("value", def.Provenance["anything"]);
        }

        // ---- F-068: a loop binding in `with:` was left unscoped ---------------------------------------

        [Fact]
        public void F068_a_loop_binding_outside_a_forEach_is_reported()
        {
            // The spec's §1.6 fact table sets `loop.<as>` and `loop.index` only on a pass of a forEach, so
            // this binding can never resolve and the callee is handed an empty value. It was accepted.
            //
            // The deferral this replaces said the check needed "the frames it would resolve against". It
            // does not: both facts this check reads (does the step have a forEach, and what does it bind)
            // come from the file. The runner was never the blocker.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Hand off",
                "```yaml step", "id: hand-off", "call:", "  workflow: other", "  with:",
                "    target: \"[[loop.table]]\"", "```", "", "Body."));
            Assert.Null(def.Error);

            var findings = WorkflowParser.V2Findings(def, null, null);
            Assert.Contains(findings, f => f.Severity == "warn"
                && f.Message.Contains("hand-off") && f.Message.Contains("does not repeat"));
        }

        [Fact]
        public void F068_a_loop_binding_naming_the_wrong_item_is_reported()
        {
            // Having a loop is not the same as having THAT value: the loop binds `table`, the reference
            // reads `measure`. Same shape as the `when:` warn one field over, and the reason the check is
            // not merely "is there a forEach".
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Hand off",
                "```yaml step", "id: hand-off", "forEach:", "  in: [a, b]", "  as: table",
                "call:", "  workflow: other", "  with:",
                "    target: \"[[loop.measure]]\"", "```", "", "Body."));
            Assert.Null(def.Error);

            var findings = WorkflowParser.V2Findings(def, null, null);
            Assert.Contains(findings, f => f.Severity == "warn" && f.Message.Contains("names its item 'table'"));
        }

        [Fact]
        public void F068_control_a_loop_binding_inside_its_own_forEach_is_not_reported()
        {
            // The over-refusal direction, which matters more than the under-refusal: a warn makes
            // check_workflow return not-ok, so a false one blocks a workflow that would have run. Both the
            // bound item name and the fixed `loop.index` have to pass clean.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Hand off",
                "```yaml step", "id: hand-off", "forEach:", "  in: [a, b]", "  as: table",
                "call:", "  workflow: other", "  with:",
                "    target: \"[[loop.table]]\"", "    pass: \"[[loop.index]]\"", "```", "", "Body."));
            Assert.Null(def.Error);

            Assert.DoesNotContain(WorkflowParser.V2Findings(def, null, null),
                f => f.Message.Contains("loop") && f.Message.Contains("hand-off"));
        }

        [Fact]
        public void F068_control_a_literal_merely_containing_the_word_loop_is_not_reported()
        {
            // The regex is ANCHORED for this reason. A substring search for 'loop.' is what previously
            // reported the legal condition `connection.database ~ '*loop.*'` as a loop read, and the same
            // mistake in this field would refuse an ordinary literal.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Hand off",
                "```yaml step", "id: hand-off", "call:", "  workflow: other", "  with:",
                "    target: a value mentioning loop.table in passing", "```", "", "Body."));
            Assert.Null(def.Error);

            Assert.DoesNotContain(WorkflowParser.V2Findings(def, null, null),
                f => f.Message.Contains("does not repeat"));
        }
    }
}
