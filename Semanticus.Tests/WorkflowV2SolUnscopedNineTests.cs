using System;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Sol's NINTH unscoped read of the format v2 slice. Three findings, none class A: nothing here
    /// changes how a frozen v1 file parses, and every fix sits in the v2-only YAML reader.
    ///
    /// F-090 (class C, silent-drop family): a block scalar that is the LAST thing in a frontmatter or a
    /// fence lost its trailing newline, so `|` and `|+` both produced the `|-` value. The author wrote a
    /// chomping indicator and the reader read a different one.
    /// F-091 (class C): a collection-shaped unknown key was reported as the wrong mistake. `verrify: [a]`
    /// said "'verrify' takes a single value, not a list" instead of naming the typo as an unknown key,
    /// because the value shape was converted before the key was judged.
    /// F-092 (class B, fixed by deletion): an alias exclusion that could never fail, deleted, with the
    /// real guard pinned by test so the behaviour is proven rather than assumed.
    /// </summary>
    public sealed class WorkflowV2SolUnscopedNineTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        // ---- F-090: a terminal block scalar lost its chomping indicator -----------------------------
        //
        // The cause: ReadYamlBlock rebuilt the block's text with string.Join("\n", ...), so the block's
        // last line carried NO line break. YAML's chomping is decided on the line breaks that follow the
        // scalar's content, so with none there, clip and keep both degrade to strip. The file on disk
        // always ends that line with a newline (a frontmatter is closed by '---', a fence by '```'), so
        // the reconstruction was simply losing a byte the author wrote.

        [Fact]
        public void F090_a_terminal_clip_block_scalar_keeps_its_trailing_newline()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "description: |",
                "  First line.",
                "  Second line.",
                "---", "## Step 1: Go", "Body."));
            Assert.Null(def.Error);
            Assert.Equal("First line.\nSecond line.\n", def.Description);
        }

        [Fact]
        public void F090_a_terminal_strip_block_scalar_has_no_trailing_newline()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "description: |-",
                "  First line.",
                "  Second line.",
                "---", "## Step 1: Go", "Body."));
            Assert.Null(def.Error);
            Assert.Equal("First line.\nSecond line.", def.Description);
        }

        [Fact]
        public void F090_a_terminal_keep_block_scalar_keeps_every_trailing_newline()
        {
            // Keep is the mode the reconstruction hurt most: the author's blank line AND the content's own
            // line break both vanished, so `|+` and `|-` were the same reader.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "description: |+",
                "  First line.",
                "  Second line.",
                "",
                "---", "## Step 1: Go", "Body."));
            Assert.Null(def.Error);
            Assert.Equal("First line.\nSecond line.\n\n", def.Description);
        }

        [Fact]
        public void F090_control_a_non_terminal_block_scalar_was_already_correct()
        {
            // The control that locates the fault. With another key after it, the scalar's last line already
            // ended in a line break, so clip already produced the trailing newline. Only the terminal
            // position was wrong, and this pins that the fix did not move the boundary.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "description: |",
                "  First line.",
                "  Second line.",
                "version: 1",
                "---", "## Step 1: Go", "Body."));
            Assert.Null(def.Error);
            Assert.Equal("First line.\nSecond line.\n", def.Description);
        }

        [Fact]
        public void F090_a_terminal_block_scalar_in_a_FENCE_keeps_its_trailing_newline()
        {
            // The same reader serves the fences, so the finding is proven where it also lives rather than
            // only where it was found. A family fix at one call site is how this project's findings recur.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go", "Body.",
                "```yaml gate", "strictness: hard", "verify:", "  - kind: bpa_clean",
                "inputs:", "  - name: approval",
                "    question: |",
                "      Approved?",
                "```"));
            Assert.Null(def.Error);
            Assert.Equal("Approved?\n", def.Steps[0].Gate!.Inputs[0].Question);
        }

        // ---- F-091: a collection-shaped unknown key was named as the wrong mistake ------------------
        //
        // The cause: SeqOfMaps converted every non-list key's value to a scalar (ScalarOf) BEFORE any
        // consumer judged the key, so a typo carrying a list or a block was refused for its SHAPE. The
        // author was told to give 'verrify' a single value, which is advice to keep the typo.

        [Fact]
        public void F091_a_list_valued_unknown_verify_key_is_named_as_unknown()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go", "Body.",
                "```yaml gate", "strictness: hard", "verify:",
                "  - kind: bpa_clean",
                "    verrify: [a]",
                "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("'verrify' is not a key this format has", def.Error);
            Assert.Contains("Gate verify keys are:", def.Error);
        }

        [Fact]
        public void F091_a_map_valued_unknown_verify_key_is_named_as_unknown()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go", "Body.",
                "```yaml gate", "strictness: hard", "verify:",
                "  - kind: bpa_clean",
                "    verrify:",
                "      deeper: 1",
                "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("'verrify' is not a key this format has", def.Error);
        }

        [Fact]
        public void F091_a_list_valued_unknown_input_key_is_named_as_unknown()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go", "Body.",
                "```yaml gate", "strictness: hard", "verify:", "  - kind: bpa_clean",
                "inputs:",
                "  - name: approval",
                "    quetsion: [a]",
                "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("'quetsion' is not a key this format has", def.Error);
            Assert.Contains("Gate input keys are:", def.Error);
        }

        [Fact]
        public void F091_a_map_valued_unknown_input_key_is_named_as_unknown()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go", "Body.",
                "```yaml gate", "strictness: hard", "verify:", "  - kind: bpa_clean",
                "inputs:",
                "  - name: approval",
                "    quetsion:",
                "      deeper: 1",
                "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("'quetsion' is not a key this format has", def.Error);
        }

        [Fact]
        public void F091_a_list_valued_unknown_slot_key_is_named_as_unknown()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "kind: template",
                "slots:",
                "  - name: measureName",
                "    hnit: [a]",
                "---", "## Step 1: Go", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("'hnit' is not a key this format has", def.Error);
            Assert.Contains("Slot keys are:", def.Error);
        }

        [Fact]
        public void F091_a_map_valued_unknown_slot_key_is_named_as_unknown()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "kind: template",
                "slots:",
                "  - name: measureName",
                "    hnit:",
                "      deeper: 1",
                "---", "## Step 1: Go", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("'hnit' is not a key this format has", def.Error);
        }

        [Fact]
        public void F091_control_a_KNOWN_key_given_a_list_is_still_refused_for_its_shape()
        {
            // The over-refusal direction, and the reason the shape check is deferred rather than deleted.
            // 'when' is a real verify key, so the author's mistake IS the shape and the message must stay
            // the shape message, naming the line.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Go", "Body.",
                "```yaml gate", "strictness: hard", "verify:",
                "  - kind: bpa_clean",
                "    when: [a]",
                "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("'when' takes a single value, not a list", def.Error);
        }

        [Fact]
        public void F091_control_a_KNOWN_key_given_a_block_is_still_refused_for_its_shape()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "kind: template",
                "slots:",
                "  - name: measureName",
                "    hint:",
                "      deeper: 1",
                "---", "## Step 1: Go", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("'hint' takes a single value, not a block of keys", def.Error);
        }

        // ---- F-092: an exclusion that could never fail ----------------------------------------------

        [Fact]
        public void F092_AnchorAlias_is_not_a_NodeEvent_so_the_deleted_clause_was_dead()
        {
            // The premise the deletion rests on, pinned rather than believed. In YamlDotNet 18.1.0
            // NodeEvent and AnchorAlias are SIBLING subclasses of ParsingEvent, so after `is NodeEvent`
            // succeeded, `is not AnchorAlias` was always true. If a later version ever makes AnchorAlias a
            // NodeEvent, this goes red and the alias guard needs looking at again.
            Assert.False(typeof(YamlDotNet.Core.Events.NodeEvent)
                .IsAssignableFrom(typeof(YamlDotNet.Core.Events.AnchorAlias)));
            Assert.Equal(typeof(YamlDotNet.Core.Events.ParsingEvent),
                typeof(YamlDotNet.Core.Events.AnchorAlias).BaseType);
        }

        [Fact]
        public void F092_an_alias_in_a_scalar_value_position_is_still_refused_by_the_real_guard()
        {
            // The real guard is the AnchorAlias branch at the bottom of ReadNode. This is the exact
            // position the deleted clause claimed to protect, so the behaviour is pinned, not assumed.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: *label", "---",
                "## Step 1: Go", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("'*label' is a YAML alias", def.Error);
        }

        [Fact]
        public void F092_an_alias_in_a_KEY_position_is_still_refused()
        {
            // The other position the same branch serves: a key is read by the same ReadNode call.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "*label: x", "---",
                "## Step 1: Go", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("alias", def.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void F092_control_an_anchor_declaration_is_still_refused_as_an_anchor()
        {
            // The neighbour the deletion must not disturb: the anchor DECLARATION guard shares the branch
            // the dead clause sat on, and its message must still say anchor, not alias.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: &label T", "---",
                "## Step 1: Go", "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("'&label' is a YAML anchor", def.Error);
        }
    }
}
