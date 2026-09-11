using System;
using System.IO;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Sol's THIRD unscoped read of the format v2 slice, against branch head 07a8bf99. Findings F-052 to
    /// F-058. One test per finding, each written from Sol's own concrete input and watched fail before a
    /// line of the parser changed.
    ///
    /// The lesson this slice has now paid for three times, and the reason these tests are shaped the way
    /// they are: a fix aimed at the ONE INSTANCE a reviewer named comes back as a new finding. F-052 is
    /// F-046 arriving at a different layer — the duplicate refusal was written where the duplicate was
    /// FOUND (a flat-list map item) rather than everywhere a duplicate is POSSIBLE. So the F-052 test does
    /// not test a gate's `strictness:`; it enumerates every scope in which this parser builds a key-to-value
    /// map, derived from the three readers that build one, and proves each refuses.
    ///
    /// F-053, F-054 and F-055 were the silent-drop family in the two shapes the (since-deleted)
    /// accounted-for line ledger provably could not see: a dropped VALUE on a line that was consumed, and
    /// a construct never recognised as a region at all. Since [T217] the v2 interiors are read by
    /// YamlDotNet and the ledger is gone; these tests now pin that each shape stays refused (by the
    /// library or by our shape rules on its model), not the wording of any refusal.
    /// </summary>
    public sealed class WorkflowV2SolUnscopedThreeTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            var path = Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), "missing file: " + path);
            return File.ReadAllText(path);
        }

        // ---- F-052: a duplicate key silently overwrites, in EVERY scope that builds a map ---------------
        //
        // Derived, not listed: one case per scope in which the format builds a key-to-value map. When
        // these were written, three hand-rolled readers each built maps and two had the hole. Since
        // [T217] every v2 map is built in ONE place (the YAML model builder), which refuses a duplicate
        // key for all scopes at once; the theory stays as the proof that no scope regresses.

        [Theory]
        // the `yaml gate` top level: Sol's own input, and the one that matters most — a hard gate turned off
        [InlineData("gate strictness", "strictness")]
        [InlineData("gate ops", "ops")]
        // the `yaml step` block and the two maps under it
        [InlineData("step id", "id")]
        [InlineData("forEach as", "as")]
        [InlineData("call workflow", "workflow")]
        [InlineData("call with", "targetMeasure")]
        // the frontmatter, flat and blocked
        [InlineData("frontmatter title", "title")]
        [InlineData("provenance entry", "derivedFrom")]
        public void F052_a_duplicate_key_is_refused_in_every_scope_that_builds_a_map(string scope, string key)
        {
            var def = WorkflowParser.Parse(DuplicateFixture(scope));
            Assert.True(def.Error != null, $"a duplicate '{key}' in {scope} was silently overwritten instead of refused");
            Assert.Contains(key, def.Error);
        }

        [Theory]
        [InlineData("gate strictness")]
        [InlineData("frontmatter title")]
        public void F052_control_v1_keeps_last_wins_for_the_same_shapes(string scope)
        {
            // v1 is frozen: last-wins is what a v1 file has always done, and a file that loaded yesterday
            // loads forever. The refusal is a v2 strictness rule and must not reach back.
            var def = WorkflowParser.Parse(DuplicateFixture(scope).Replace("schemaVersion: 2\n", ""));
            Assert.True(def.Error == null, "a v1 file with a duplicate key was refused: " + def.Error);
            Assert.Equal(1, def.SchemaVersion);
        }

        [Fact]
        public void F052_control_the_same_key_in_two_different_steps_is_not_a_duplicate()
        {
            // The refusal is per map, not per file. Two steps each declaring `id:` is ordinary.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: One", "```yaml step", "id: one", "```", "", "Body.",
                "## Step 2: Two", "```yaml step", "id: two", "```", "", "Body."));
            Assert.True(def.Error == null, "two steps each with their own id were refused: " + def.Error);
            Assert.Equal(new[] { "one", "two" }, def.Steps.Select(s => s.Id).ToArray());
        }

        private static string DuplicateFixture(string scope) => scope switch
        {
            "gate strictness" => Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Check", "Body.",
                "```yaml gate", "strictness: hard", "strictness: off", "verify:", "  - kind: bpa_clean", "```"),
            "gate ops" => Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Check", "Body.",
                "```yaml gate", "ops: [bpa_scan]", "ops: [get_dax]", "strictness: hard",
                "verify:", "  - kind: bpa_clean", "```"),
            "step id" => Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: first", "id: second", "```", "", "Body."),
            "forEach as" => Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach:", "  in: [Sales]", "  as: table", "  as: measure",
                "```", "", "Body."),
            "call workflow" => Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Hand off",
                "```yaml step", "id: hand-off", "call:", "  workflow: one", "  workflow: two",
                "```", "", "Body."),
            "call with" => Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Hand off",
                "```yaml step", "id: hand-off", "call:", "  workflow: other", "  with:",
                "    targetMeasure: inputs.one", "    targetMeasure: inputs.two", "```", "", "Body."),
            "frontmatter title" => Md(
                "---", "schemaVersion: 2", "name: t", "title: First", "title: Second", "---",
                "## Step 1: Do", "Body."),
            "provenance entry" => Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "provenance:",
                "  derivedFrom: one", "  derivedFrom: two", "---", "## Step 1: Do", "Body."),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "no fixture"),
        };

        // ---- F-053: a block key throws its inline value away ------------------------------------------

        [Theory]
        [InlineData("forEach: author-value", "  in: [Sales]", "  as: table", "forEach")]
        [InlineData("call: author-value", "  workflow: other", "  returns: [x]", "call")]
        public void F053_a_block_key_with_a_value_on_the_same_line_is_refused(
            string blockLine, string childOne, string childTwo, string key)
        {
            // Sol's input verbatim. The line IS consumed, so the accounted-for ledger sees nothing wrong;
            // only the value on it is dropped. Same family as the inline `with: { a: b }` map (F-041), and
            // this is the same reader, so the refusal belongs in the reader rather than at a third call site.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", blockLine, childOne, childTwo, "```", "", "Body."));
            Assert.True(def.Error != null, $"'{blockLine}' silently discarded its value");
            // [T217]: a scalar with a more-indented block beneath it is invalid YAML, so YamlDotNet
            // refuses it naming the line. The value cannot be dropped any more; it cannot even parse.
            Assert.Contains("line", def.Error);
        }

        [Fact]
        public void F053_control_a_block_key_with_no_value_still_parses()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "forEach:", "  in: [Sales]", "  as: table", "```", "", "Body."));
            Assert.True(def.Error == null, "a legal block-form forEach was refused: " + def.Error);
            Assert.Equal("table", def.Steps[0].ForEach!.As);
        }

        // ---- F-054: an INDENTED fence in the preamble ------------------------------------------------

        [Theory]
        [InlineData("gate")]
        [InlineData("step")]
        public void F054_an_indented_fence_before_the_first_step_is_refused(string kind)
        {
            // F-047 refused a LEFT-ALIGNED fence in the preamble. The indented one is the same loss through
            // the same door: not a fence to the anchored regexes, not inside any step, so it is neither a
            // region the ledger asserts nor prose anyone reads. One leading space and the hard gate is gone.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "Some preamble prose.",
                " ```yaml " + kind, " strictness: hard", " verify:", "   - kind: bpa_clean", " ```",
                "## Step 1: Do", "Body."));
            Assert.True(def.Error != null, $"an indented '{kind}' fence in the preamble was discarded whole");
            Assert.Contains(kind, def.Error);
        }

        [Fact]
        public void F054_control_ordinary_preamble_prose_stays_ordinary()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "Some preamble prose, indented code and all:",
                "    yaml gate is written about here, not opened.",
                "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "ordinary preamble prose was refused: " + def.Error);
        }

        // ---- F-055: an inline list whose quote never closes -------------------------------------------

        [Fact]
        public void F055_an_inline_list_with_an_unclosed_quote_is_refused()
        {
            // Sol's input verbatim, and this one is OURS: the quote-aware split written last round to fix
            // F-048 tracks an open quote and never checks that it closed, so the unmatched quote is simply
            // removed and two tags come back as though the author wrote them.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "tags: [finance, \"monthly, sales]", "---",
                "## Step 1: Do", "Body."));
            Assert.True(def.Error != null, "an inline list with an unclosed quote was accepted");
            Assert.Contains("line", def.Error);   // [T217]: the library's refusal names the line
        }

        [Fact]
        public void F055_control_a_closed_quote_still_holds_a_comma()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "tags: [finance, \"monthly, sales\"]", "---",
                "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a legal quoted list item was refused: " + def.Error);
            // The sibling check finding 3 asked for: this ternary had the SAME expression on both branches,
            // so `def.Steps.Length` was read and discarded. Not a void guard (the assertion below is real),
            // but the same family of decoration that makes a test read as stronger than it is.
            Assert.Equal(new[] { "finance", "monthly, sales" }, def.Tags);
        }

        [Fact]
        public void F055_control_v1_keeps_the_unclosed_quote_behaviour()
        {
            // F-067: this asserted ONLY that the file parsed, while being NAMED for the v1 tag behaviour it
            // was the sole guard on. Every parsed tag could change under it and it stayed green, which is
            // exactly what happened: the quote-aware split (F-065) turned this input from three tags into
            // two and this test did not move. "Did not throw" is not a behaviour.
            //
            // The expected value is the PINNED pre-v2 parser's, not a reading of today's code: v1 splits on
            // every comma and strips quote characters afterwards, so `[finance, "monthly, sales]` is the
            // three tags below. The same v1 rule is pinned end-to-end by the golden through
            // fixtures/workflow-v1-shapes/quoted-comma-in-list.md.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "tags: [finance, \"monthly, sales]", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a v1 file with an unclosed quote was refused: " + def.Error);
            Assert.Equal(new[] { "finance", "monthly", "sales" }, def.Tags);
        }

        // ---- F-056: a post-quote tail beginning with '#' but with no space before it -------------------

        [Fact]
        public void F056_a_hash_tail_with_no_space_before_it_is_refused()
        {
            // This parser's OWN comment rule requires the whitespace (SplitKeyValue's comment-only branch and
            // the " #" cut both do). The post-quote check exempted anything starting with '#', so a second
            // key rides in on a line that is not a comment by this parser's own definition and vanishes.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: \"Quarterly\"#owner: finance", "---",
                "## Step 1: Do", "Body."));
            Assert.True(def.Error != null, "'title: \"Quarterly\"#owner: finance' silently dropped its tail");
            // [T217]: content after a closing quote is a YAML error; the refusal names the line, and the
            // tail can no longer vanish because the file no longer parses.
            Assert.Contains("line", def.Error);
        }

        [Fact]
        public void F056_control_a_real_comment_after_a_quoted_value_is_still_allowed()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: \"Quarterly\" # the owner is finance", "---",
                "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a legal comment after a quoted value was refused: " + def.Error);
            Assert.Equal("Quarterly", def.Title);
        }

        // ---- F-057: a loop fact checked against the loop's actual `as:` binding ------------------------

        [Fact]
        public void F057_a_loop_fact_that_names_no_binding_is_warned_about()
        {
            // Sol's input verbatim. `loop.measure` can never be set: this loop binds `as: table`, so at run
            // time the fact is unknown, every comparison against it is false, and the step SILENTLY does not
            // run. That is the exact hazard the misspelled-input warn already covers one fact-root over.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "when: loop.measure == 'Sales'",
                "forEach:", "  in: [Sales]", "  as: table", "```", "", "Body."));
            Assert.True(def.Error == null, "the fixture itself does not parse: " + def.Error);

            var findings = WorkflowParser.V2Findings(def, new[] { def }, Array.Empty<WorkflowDef>());
            Assert.Contains(findings, f => f.Message.Contains("loop.measure") && f.Message.Contains("table"));
        }

        [Theory]
        [InlineData("loop.table == 'Sales'")]   // the bound item
        [InlineData("loop.index > 0")]          // the loop's own counter, always set
        public void F057_control_a_bound_loop_fact_is_not_warned_about(string when)
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Do",
                "```yaml step", "id: do-it", "when: " + when,
                "forEach:", "  in: [Sales]", "  as: table", "```", "", "Body."));
            Assert.True(def.Error == null, "the fixture itself does not parse: " + def.Error);

            var findings = WorkflowParser.V2Findings(def, new[] { def }, Array.Empty<WorkflowDef>());
            Assert.DoesNotContain(findings, f => f.Message.Contains("loop"));
        }

        // ---- F-058: the spec's two stale counts of the reserved destination keys -----------------------

        [Fact]
        public void F058_the_spec_counts_the_reserved_destination_keys_correctly()
        {
            // A count-shaped claim in the spec that disagrees with the set the code enumerates. The code is
            // right; section 1.8's own table, its "single design constraint all nine share" sentence and the
            // acceptance check all say nine. Pinned rather than merely corrected, because this is the third
            // count-versus-set drift this document has had (section 0 records the first two).
            Assert.Equal(9, WorkflowParser.ReservedDestinationKeys.Count);

            var spec = RepoFile("docs", "workflow-canvas-spec.md");
            Assert.DoesNotContain("eight destination keys", spec);
            Assert.DoesNotContain("for any of the eight.", spec);
            Assert.Contains("nine destination keys", spec);
            Assert.Contains("for any of the nine.", spec);
        }
    }
}
