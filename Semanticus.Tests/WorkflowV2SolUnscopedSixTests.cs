using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Sol's SIXTH unscoped read of the format v2 slice, against branch head ddaf7a2f. Findings F-071 to
    /// F-075. No class A: nothing here changes how a frozen v1 file parses.
    ///
    /// Two are OVER-refusals (F-073, F-074), which is the direction that matters more than under-refusal:
    /// a refusal blocks a file that would have run, and both of these reported the mistake as something
    /// other than what the author actually wrote. Each carries a control proving the legal input parses,
    /// and F-073 and F-074 each carry a v1 control too, because both fixes sit in a reader v1 shares and
    /// that is exactly how the fifth read's two class-A breaks were made.
    /// </summary>
    public sealed class WorkflowV2SolUnscopedSixTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        // ---- F-071: the README row check skipped a name it did not recognise -------------------------

        [Fact]
        public void F071_a_row_naming_a_nonexistent_test_is_refused()
        {
            // The hole: names were matched by a `[RU]\d+` PREFIX pattern, so `F1_fake_test` matched nothing,
            // was never added to the list, and was skipped rather than refused. A row could name one real
            // test and any number of fictional ones and pass.
            var readme = "| `explicit-v1.md` | what it pins | golden; `R11_real_test`; `F1_fake_test` |";
            var source = "public void R11_real_test() { var d = Fixture(\"explicit-v1\"); }\n"
                       + "public void Something_else() { }";

            var problems = WorkflowV1CompatibilityTests.ReadmeTableProblems(
                readme, source, new[] { "explicit-v1.md" });

            Assert.Contains(problems, p => p.Contains("F1_fake_test") && p.Contains("not in"));
        }

        [Fact]
        public void F071_control_a_row_naming_only_real_tests_is_clean()
        {
            // The over-refusal direction. Backticked FORMAT text in the other cells must not be read as a
            // test name, which is why the scan is scoped to the last cell.
            var readme = "| `explicit-v1.md` | an explicit `schemaVersion: 1` parses | golden; `R11_real_test` |";
            var source = "public void R11_real_test() { var d = Fixture(\"explicit-v1\"); }";

            Assert.Empty(WorkflowV1CompatibilityTests.ReadmeTableProblems(
                readme, source, new[] { "explicit-v1.md" }));
        }

        [Fact]
        public void F071_a_named_test_that_does_not_read_the_fixture_is_refused()
        {
            var readme = "| `explicit-v1.md` | what it pins | golden; `R11_real_test` |";
            var source = "public void R11_real_test() { /* copied from explicit-v1.md */ var d = Parse(\"...\"); }";

            Assert.Contains(WorkflowV1CompatibilityTests.ReadmeTableProblems(
                readme, source, new[] { "explicit-v1.md" }), p => p.Contains("does not call Fixture"));
        }

        [Fact]
        public void F071_a_fixture_on_disk_with_no_row_is_refused()
        {
            Assert.Contains(WorkflowV1CompatibilityTests.ReadmeTableProblems(
                "| `explicit-v1.md` | x | golden; `R11_real_test` |",
                "public void R11_real_test() { var d = Fixture(\"explicit-v1\"); }",
                new[] { "explicit-v1.md", "orphan.md" }), p => p.Contains("orphan.md") && p.Contains("no README row"));
        }

        // ---- F-072: input availability ignored ORDER --------------------------------------------------

        [Fact]
        public void F072_a_when_reading_an_answer_collected_later_is_reported()
        {
            // Step 1 asks about an answer Step 2 collects. `when:` is checked BEFORE the step runs, so the
            // fact is unknown, every comparison is false, and Step 1 silently never runs. Run-wide
            // availability could not see it: the name IS collected, just not yet.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Act", "```yaml step", "id: act", "when: inputs.approval.answered", "```", "", "Body.",
                "## Step 2: Ask", "Body.",
                "```yaml gate", "strictness: hard", "inputs:", "  - name: approval",
                "    question: Approved?", "    type: verification", "```"));
            Assert.Null(def.Error);

            Assert.Contains(WorkflowParser.V2Findings(def, null, null),
                f => f.Severity == "warn" && f.Message.Contains("act") && f.Message.Contains("not collected until"));
        }

        [Fact]
        public void F072_control_an_answer_collected_earlier_is_not_reported()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "strictness: hard", "inputs:", "  - name: approval",
                "    question: Approved?", "    type: verification", "```",
                "## Step 2: Act", "```yaml step", "id: act", "when: inputs.approval.answered", "```", "", "Body."));
            Assert.Null(def.Error);

            Assert.DoesNotContain(WorkflowParser.V2Findings(def, null, null),
                f => f.Message.Contains("not collected until") || f.Message.Contains("no gate collects"));
        }

        [Fact]
        public void F072_a_step_cannot_see_its_OWN_gates_answer()
        {
            // The subtle half: a step's own gate runs during the step, after its condition was checked.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "## Step 1: Act", "```yaml step", "id: act", "when: inputs.approval.answered", "```", "", "Body.",
                "```yaml gate", "strictness: hard", "inputs:", "  - name: approval",
                "    question: Approved?", "    type: verification", "```"));
            Assert.Null(def.Error);

            Assert.Contains(WorkflowParser.V2Findings(def, null, null),
                f => f.Severity == "warn" && f.Message.Contains("not collected until"));
        }

        // ---- F-073: the inline-comment cut ignored quoting --------------------------------------------

        [Fact]
        public void F073_a_hash_inside_a_quoted_list_item_is_not_a_comment()
        {
            // `tags: ["finance # monthly", sales]` was cut at the hash to `["finance`, and then refused by
            // the unclosed-list check — legal content, refused, and reported as a different mistake than
            // the author made.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: [\"finance # monthly\", sales]", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a legal quoted hash was refused: " + def.Error);
            Assert.Equal(new[] { "finance # monthly", "sales" }, def.Tags);
        }

        [Fact]
        public void F073_control_an_unquoted_trailing_comment_is_still_cut()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: [finance, sales] # the teams this covers", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a legal trailing comment was refused: " + def.Error);
            Assert.Equal(new[] { "finance", "sales" }, def.Tags);
        }

        [Fact]
        public void F073_control_v1_still_truncates_at_the_hash_wherever_it_falls()
        {
            // THE FROZEN HALF. The pinned pre-v2 parser cuts at the first " #" with no idea about quotes,
            // so this v1 file has always been truncated to `["finance` and the tag reads with the literal
            // bracket. Making v1 smarter would be the fifth read's class-A mistake a third time.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "tags: [\"finance # monthly\", sales]", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a v1 file was refused: " + def.Error);
            Assert.Equal(new[] { "[\"finance" }, def.Tags);
        }

        // ---- F-074: an apostrophe was read as an opening quote ----------------------------------------

        [Fact]
        public void F074_an_apostrophe_inside_a_word_is_not_an_opening_quote()
        {
            // `[O'Reilly, finance]` — the ' opened a quote, swallowed the comma, never closed, and the file
            // was refused. Accepted by v1, refused by v2: the two-bodies divergence F-065's fix created.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: [O'Reilly, finance]", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a legal apostrophe was refused: " + def.Error);
            Assert.Equal(new[] { "O'Reilly", "finance" }, def.Tags);
        }

        [Fact]
        public void F074_control_a_leading_quote_still_quotes_the_item()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: ['monthly, sales', finance]", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a legal quoted item was refused: " + def.Error);
            Assert.Equal(new[] { "monthly, sales", "finance" }, def.Tags);
        }

        [Fact]
        public void F074_control_a_leading_quote_that_never_closes_is_still_refused()
        {
            // F-055's refusal must survive: the fix narrows WHEN a quote opens, not whether it must close.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "tags: [finance, \"monthly, sales]", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error != null, "an unclosed leading quote was accepted");
            Assert.Contains("line", def.Error);   // [T217]: the library's refusal names the line
        }

        [Fact]
        public void F074_control_v1_still_strips_the_apostrophe_from_the_ends()
        {
            // The frozen half again: v1 splits naively and then Trim()s quote characters off BOTH ends, so
            // a trailing apostrophe is deleted. Ugly, and v1's, and not ours to fix.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "tags: [O'Reilly, 'finance']", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a v1 file was refused: " + def.Error);
            Assert.Equal(new[] { "O'Reilly", "finance" }, def.Tags);
        }

        // ---- F-075: two comments still taught the corrected-away claim --------------------------------

        [Fact]
        public void F075_no_source_comment_still_claims_seven_scopes_or_one_open_map()
        {
            // Asserted over the SOURCE because the defect IS the source text: the spec was corrected in the
            // fifth read and these two comments were not, so the wrong version survived in the place a
            // builder actually reads. Same shape as F-069 one read earlier.
            var root = System.IO.Path.GetDirectoryName(typeof(WorkflowV1CompatibilityTests).Assembly.Location);
            var dir = new System.IO.DirectoryInfo(root!);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find the repo root");

            foreach (var rel in new[] { "Semanticus.Engine/WorkflowParser.cs", "Semanticus.Engine/Workflow.cs" })
            {
                var text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                Assert.DoesNotContain("seven closed", text, System.StringComparison.Ordinal);
                Assert.DoesNotContain("The one OPEN map", text, System.StringComparison.Ordinal);
                Assert.DoesNotContain("the one open map", text, System.StringComparison.Ordinal);
            }
        }
    }
}
