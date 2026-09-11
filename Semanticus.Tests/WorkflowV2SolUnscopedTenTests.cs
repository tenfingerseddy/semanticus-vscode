using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Sol's TENTH unscoped read of the format v2 slice. Two findings, both v2-only: nothing here changes
    /// how a frozen v1 file parses.
    ///
    /// F-093 (class A, silent-drop family): `schemaVersion: 2` followed by an indented continuation line
    /// parses to the scalar "2 extra", but the v2 model walk ignored the parsed value entirely, so the raw
    /// scan's literal 2 won and the author's `extra` disappeared with nothing said. The raw scan stays
    /// dumb (read eight's ruling); the refusal belongs to the model walk, which is where the value the
    /// YAML reader actually built is in hand.
    /// F-094 (class C, wrong-diagnosis family): a BLOCK key given a value on the same line was reported as
    /// empty, and the advice told the author to add child keys under a scalar it never asked them to
    /// remove, which is invalid YAML. `forEach:` was the member the read named; `call:`, `with:` and
    /// `provenance:` sat on the same path, so the whole family moves to one body at once.
    ///
    /// F-095 (read ELEVEN, class C, wording): F-094's replacement wording quoted the value back as
    /// `forEach: author-value`, which is text the author never wrote when the value came from a block
    /// scalar, and it taught that only indented child lines are legal, which the accepted flow map
    /// `forEach: { in: [Sales], as: table }` contradicts. The F-094 tests move with the wording; the
    /// flow-map controls below are the proof that the message may promise that form.
    /// </summary>
    public sealed class WorkflowV2SolUnscopedTenTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        // ---- F-093: a version line with a continuation ----------------------------------------------

        [Fact]
        public void F093_a_schemaVersion_with_an_indented_continuation_is_refused()
        {
            // YamlDotNet reads `schemaVersion: 2` + `  extra` as one plain scalar, "2 extra". The scan
            // reads the literal 2 and selects the v2 parser, which is all the scan is for; the model walk
            // must then judge the value the reader really built, or the author's `extra` is dropped.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "  extra", "name: t", "title: T", "---",
                "## Step 1: Go", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("schemaVersion", def.Error);
            Assert.Contains("not a whole number", def.Error);
            Assert.Contains("extra", def.Error);
        }

        [Fact]
        public void F093_the_continuation_is_refused_by_the_v2_walk_not_read_as_version_1()
        {
            // The half of the promise that is easy to lose: the scan still SELECTS v2 from the literal 2.
            // If this ever came back as version 1, the fix had migrated into the scan.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "  extra", "name: t", "title: T", "---",
                "## Step 1: Go", "Body."));
            Assert.Equal(2, def.SchemaVersion);
        }

        [Fact]
        public void F093_control_a_plain_schemaVersion_2_still_parses()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Go", "Body."));
            Assert.True(def.Error == null, "a legal v2 frontmatter was refused: " + def.Error);
            Assert.Equal(2, def.SchemaVersion);
        }

        // ---- F-094: a block key given a value on the same line ---------------------------------------

        [Fact]
        public void F094_a_forEach_with_a_value_on_the_same_line_names_the_value()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach: author-value", "```", "", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("author-value", def.Error);
            Assert.Contains("takes a map", def.Error);
            Assert.DoesNotContain("is empty", def.Error);
        }

        [Fact]
        public void F094_a_call_with_a_value_on_the_same_line_names_the_value()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "call: other-workflow", "```", "", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("other-workflow", def.Error);
            Assert.Contains("takes a map", def.Error);
        }

        [Fact]
        public void F094_a_call_with_given_a_value_on_the_same_line_names_the_value()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "call:", "  workflow: other", "  with: target-value",
                "```", "", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("target-value", def.Error);
            Assert.Contains("takes a map", def.Error);
        }

        [Fact]
        public void F094_a_provenance_given_a_value_on_the_same_line_names_the_value()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "provenance: hand-written", "---",
                "## Step 1: Go", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("hand-written", def.Error);
            Assert.Contains("takes a map", def.Error);
        }

        [Fact]
        public void F094_a_bare_forEach_is_still_refused_as_empty()
        {
            // The other half of the diagnosis: with nothing after the colon, "empty" is the TRUE report,
            // and the advice to add child keys is the right advice.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach:", "```", "", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("is empty", def.Error);
            Assert.Contains("in:", def.Error);
            Assert.Contains("as:", def.Error);
        }

        [Fact]
        public void F094_control_a_bare_with_is_still_an_empty_hand_over()
        {
            // The bare-section ruling (read three), which the shared body must not undo.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "call:", "  workflow: other", "  with:", "```", "", "Body."));
            Assert.True(def.Error == null, "a bare 'with:' was refused: " + def.Error);
            Assert.Empty(def.Steps[0].Call.With);
        }

        [Fact]
        public void F094_control_a_bare_provenance_is_still_an_empty_bag()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "provenance:", "---",
                "## Step 1: Go", "Body."));
            Assert.True(def.Error == null, "a bare 'provenance:' was refused: " + def.Error);
            Assert.Empty(def.Provenance);
        }

        [Fact]
        public void F094_control_a_real_forEach_block_still_parses()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach:", "  in: [Sales, Product]", "  as: table",
                "```", "", "Body."));
            Assert.True(def.Error == null, "a legal forEach was refused: " + def.Error);
            Assert.Equal("table", def.Steps[0].ForEach.As);
        }

        [Fact]
        public void F094_control_a_real_call_block_still_parses()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "call:", "  workflow: other", "  with:", "    target: Sales",
                "```", "", "Body."));
            Assert.True(def.Error == null, "a legal call was refused: " + def.Error);
            Assert.Equal("other", def.Steps[0].Call.Workflow);
            Assert.Equal("Sales", def.Steps[0].Call.With["target"]);
        }

        // ---- F-095: the block-scalar shape, diagnosed without inventing the author's line -------------

        [Fact]
        public void F095_a_block_scalar_on_forEach_does_not_invent_a_line_the_author_never_wrote()
        {
            // `forEach: |-` with the text on the NEXT line is a scalar too, so it lands on the same
            // branch. The old wording rendered it back as "forEach: author-value" and called that a
            // value placed on the key line, which is text the author never wrote.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach: |-", "  author-value", "```", "", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("author-value", def.Error);          // the value it really saw is named
            Assert.DoesNotContain("forEach: author-value", def.Error);
            Assert.Contains("takes a map", def.Error);           // the true diagnosis
            Assert.Contains("flow form", def.Error);             // and the one-line spelling stays legal
            // The whole sentence, pinned once: a wording finding is only fixed if the wording is
            // the thing under test.
            Assert.Equal(
                "Step 1: 'forEach:' takes a map, and it was given the value 'author-value' (line 9). "
                + "It needs 'in:' (the list) and 'as:' (the name for one item), each on its own "
                + "indented line. A map may also be written on one line in flow form, like "
                + "{ key: value }.",
                def.Error);
        }

        [Fact]
        public void F095_control_a_scalar_on_the_same_line_still_names_its_value()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach: author-value", "```", "", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("author-value", def.Error);
            Assert.Contains("takes a map", def.Error);
        }

        [Fact]
        public void F095_control_a_flow_map_forEach_still_parses()
        {
            // The form the old wording contradicted when it taught that only indented child lines are
            // legal. If this ever stops parsing, the message must stop promising it.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach: { in: [Sales], as: table }", "```", "", "Body."));
            Assert.True(def.Error == null, "a flow-map forEach was refused: " + def.Error);
            Assert.Equal("table", def.Steps[0].ForEach.As);
        }

        [Fact]
        public void F095_control_a_flow_map_call_still_parses()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "call: { workflow: other, with: { target: Sales } }",
                "```", "", "Body."));
            Assert.True(def.Error == null, "a flow-map call was refused: " + def.Error);
            Assert.Equal("other", def.Steps[0].Call.Workflow);
            Assert.Equal("Sales", def.Steps[0].Call.With["target"]);
        }
    }
}
