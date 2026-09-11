using System;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The automated reviewer's findings on PR #301, each reproduced before it was fixed.
    ///
    /// These exist after eight review rounds for one reason worth writing down: every Sol round was scoped to
    /// one dimension, which is why they converged, and it also meant gate section parsing was never looked at
    /// again after round 1. A fresh read of the whole diff with no scope found three things, one of them P1 in
    /// the parser. Scoped review and unscoped review are not substitutes for each other.
    /// </summary>
    public sealed class WorkflowV2Pr301ReviewTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        // ---- P1: an indented gate section is silently discarded ---------------------------------------

        [Fact]
        public void C1_an_indented_verify_section_is_refused_in_v2()
        {
            // The reviewer's input. `verify:` is indented one level too far after a scalar, so `current` is
            // null when its lines arrive and the WHOLE section is dropped: the gate the author wrote loses a
            // hard verification and the file is accepted. That is round 1's finding 3 arriving by another
            // door, and it is the worst failure this format can have, because the run then proves nothing
            // while looking like it proved something.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Check", "Body.",
                "```yaml gate", "strictness: hard", "  verify:", "    - kind: bpa_clean", "```"));
            Assert.True(def.Error != null, "an indented 'verify:' section was silently discarded");
            // [T217]: the refusal is YamlDotNet's (bad indentation is a YAML error), so it names the LINE
            // rather than the key. Behaviour asserted: refused, located. Message equality was never owed.
            Assert.Contains("line", def.Error);
        }

        [Fact]
        public void C1_family_any_indented_line_with_no_open_section_is_refused_in_v2()
        {
            // The family, not the instance: it is not about `verify:` in particular. Any indented content
            // arriving while no list section is open is content the parser is about to drop on the floor.
            foreach (var stray in new[] { "  inputs:", "  - name: approval", "  anything: at all" })
            {
                var def = WorkflowParser.Parse(Md(
                    "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Check", "Body.",
                    "```yaml gate", "strictness: hard", stray, "```"));
                Assert.True(def.Error != null, $"v2 silently dropped the stray indented line '{stray}'");
            }
        }

        [Fact]
        public void C1_control_a_correctly_indented_gate_still_parses()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Check", "Body.",
                "```yaml gate", "strictness: hard", "verify:", "  - kind: bpa_clean",
                "inputs:", "  - name: approval", "    question: Approved?", "```"));
            Assert.True(def.Error == null, "a well-formed gate was refused: " + def.Error);
            Assert.Equal(new[] { "bpa_clean" }, def.Steps[0].Gate!.Verify.Select(v => v.Kind).ToArray());
            Assert.Equal(new[] { "approval" }, def.Steps[0].Gate!.Inputs.Select(i => i.Name).ToArray());
        }

        [Fact]
        public void C1_control_v1_still_ignores_an_indented_section_exactly_as_it_always_did()
        {
            // v1-forever. The dropped section is the behaviour a v1 file has always had, so it keeps it, and
            // the file keeps loading. Only v2 refuses.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "---", "## Step 1: Check", "Body.",
                "```yaml gate", "strictness: hard", "  verify:", "    - kind: bpa_clean", "```"));
            Assert.Null(def.Error);
            Assert.Empty(def.Steps[0].Gate?.Verify ?? Array.Empty<VerifySpec>());
        }

        // ---- P2: the unclosed-list refusal reached only two of eight list fields ----------------------

        [Theory]
        // frontmatter
        [InlineData("schemaVersion: 2|name: t|title: T|tags: [finance", "tags")]
        [InlineData("schemaVersion: 2|name: t|title: T|triggers: [create_measure", "triggers")]
        public void C2_every_v2_frontmatter_list_refuses_an_unclosed_list(string pipeSeparated, string key)
        {
            var def = WorkflowParser.Parse(Md(
                new[] { "---" }.Concat(pipeSeparated.Split('|'))
                    .Concat(new[] { "---", "## Step 1: Do", "Body." }).ToArray()));
            Assert.True(def.Error != null, $"v2 accepted an unclosed list on '{key}' and kept the literal '[...' as an item");
            // [T217]: an unclosed flow sequence is refused by YamlDotNet, naming the line, not the key.
            Assert.Contains("line", def.Error);
        }

        [Fact]
        public void C2_a_v2_gate_ops_list_refuses_an_unclosed_list()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do", "Body.",
                "```yaml gate", "ops: [create_measure", "```"));
            Assert.True(def.Error != null, "v2 accepted an unclosed gate ops list");
            Assert.Contains("line", def.Error);   // [T217]: the library's refusal names the line
        }

        [Fact]
        public void C2_a_v2_slot_values_list_refuses_an_unclosed_list()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "kind: template", "title: T",
                "slots:", "  - name: region", "    question: Which region?", "    values: [emea, apac",
                "---", "## Step 1: Do", "Body about {{region}}."));
            Assert.True(def.Error != null, "v2 accepted an unclosed slot values list");
            Assert.Contains("line", def.Error);   // [T217]: the library's refusal names the line
        }

        [Theory]
        [InlineData("pinnedShapes")]
        [InlineData("openShapes")]
        public void C2_a_v2_verify_shape_list_refuses_an_unclosed_list(string key)
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do", "Body.",
                "```yaml gate", "inputs:", "  - name: rewrite", "    question: The rewrite?", "    type: text",
                "verify:", "  - kind: dax_equivalence", "    probe: rewrite", $"    {key}: [grand_total",
                "```"));
            Assert.True(def.Error != null, $"v2 accepted an unclosed {key} list");
            Assert.Contains("line", def.Error);   // [T217]: the library's refusal names the line
        }

        [Fact]
        public void C2_control_v1_keeps_reading_every_unclosed_list_exactly_as_it_did()
        {
            // The reason the refusal was put at the call sites rather than inside ParseInlineList in round 1,
            // and the reason it still is not inside it: this helper reads v1 `triggers`, `tags`, `ops` and
            // slot `values` too, and a v1 file that yields the literal item '[finance' has to keep doing so.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "tags: [finance", "triggers: [create_measure", "---",
                "## Step 1: Do", "Body.", "```yaml gate", "ops: [create_measure", "```"));
            Assert.Null(def.Error);
            Assert.Equal(new[] { "[finance" }, def.Tags);
            Assert.Equal(new[] { "[create_measure" }, def.Triggers);
            Assert.Equal(new[] { "[create_measure" }, def.Steps[0].Ops);
        }

        [Fact]
        public void C2_control_a_closed_list_is_still_read_in_v2()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "tags: [finance, monthly]",
                "triggers: [create_measure]", "---", "## Step 1: Do", "Body."));
            Assert.Null(def.Error);
            Assert.Equal(new[] { "finance", "monthly" }, def.Tags);
            Assert.Equal(new[] { "create_measure" }, def.Triggers);
        }

        // ---- P2: 'loop.' matched inside a string literal ----------------------------------------------

        [Fact]
        public void C3_a_literal_containing_loop_is_not_a_loop_reference()
        {
            // The check searched the raw `when:` TEXT, so any condition whose literal happens to contain
            // 'loop.' was reported as reading a loop value outside a loop. That warn makes check_workflow
            // return not-ok, so a valid workflow is blocked from admission by a substring.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Do",
                "```yaml step", "id: act", "when: connection.database ~ '*loop.*'", "```", "", "Body."));
            Assert.True(def.Error == null, "the file did not parse: " + def.Error);
            var findings = WorkflowParser.V2Findings(def, null, null);
            Assert.DoesNotContain(findings, f => f.Message.Contains("loop value"));
        }

        [Fact]
        public void C3_control_a_real_loop_reference_outside_a_loop_is_still_warned()
        {
            // The control: reading loop.x on a step that does not repeat is still inert at run time, so the
            // warn has to survive. Fixing the false positive must not cost the true one.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Do",
                "```yaml step", "id: act", "when: loop.table == 'Sales'", "```", "", "Body."));
            Assert.True(def.Error == null, "the file did not parse: " + def.Error);
            Assert.Contains(WorkflowParser.V2Findings(def, null, null),
                f => f.Severity == "warn" && f.Message.Contains("loop value"));
        }
    }
}
