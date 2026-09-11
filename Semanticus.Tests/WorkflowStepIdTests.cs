using System;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Format v2 stable step ids (docs/workflow-canvas-spec.md §1.4; acceptance checks 7-9). Step identity
    /// today is derived from the heading number (<c>WorkflowParser.cs:154,156</c>), so moving a step changes
    /// its id. v2 puts identity in an optional <c>yaml step</c> fence; absent, the id stays <c>step-N</c>
    /// byte-identically, so a v2 file that does not opt in addresses exactly like a v1 file.
    /// </summary>
    public sealed class WorkflowStepIdTests
    {
        private static string Md(params string[] lines) => string.Join("\n", lines);

        /// <summary>A v2 file of N steps; <paramref name="fences"/>[i] is step i+1's fence body (null = no fence).</summary>
        private static string Steps(params string[][] fences)
        {
            var lines = new System.Collections.Generic.List<string> { "---", "schemaVersion: 2", "name: t", "title: T", "---", "" };
            for (int n = 1; n <= fences.Length; n++)
            {
                lines.Add($"## Step {n}: Do {n}");
                lines.Add("");
                if (fences[n - 1] != null)
                {
                    lines.Add("```yaml step");
                    lines.AddRange(fences[n - 1]);
                    lines.Add("```");
                    lines.Add("");
                }
                lines.Add($"Body {n}.");
                lines.Add("");
            }
            return Md(lines.ToArray());
        }

        // ---- check 7 ---------------------------------------------------------------------------------

        [Fact]
        public void An_explicit_id_becomes_the_step_id()
        {
            var def = WorkflowParser.Parse(Steps(null, null, new[] { "id: prove-rewrite" }));
            Assert.Null(def.Error);
            Assert.Equal("prove-rewrite", def.Steps[2].Id);
            Assert.Equal(3, def.Steps[2].Number);       // the heading number stays the reading order
        }

        [Fact]
        public void An_absent_id_stays_step_n_byte_identically()
        {
            var def = WorkflowParser.Parse(Steps(null, new[] { "when: inputs.a.answered" }, null));
            Assert.Null(def.Error);
            Assert.Equal(new[] { "step-1", "step-2", "step-3" }, def.Steps.Select(s => s.Id).ToArray());
        }

        [Fact]
        public void An_id_must_be_lower_kebab()
        {
            // The pattern is the spec's, ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ (§1.4). An earlier revision of the
            // spec wrote ^[a-z][a-z0-9-]*$, which accepted a trailing hyphen and a doubled one; it was
            // tightened before any file could rely on it. Both are refused here so the tightening cannot
            // quietly regress.
            foreach (var bad in new[] { "Prove-Rewrite", "3-prove", "prove_rewrite", "-prove", "prove-", "prove--rewrite" })
            {
                var def = WorkflowParser.Parse(Steps(new[] { "id: " + bad }));
                Assert.True(def.Error != null, $"id '{bad}' was accepted");
                Assert.Contains(bad, def.Error);
            }
        }

        // ---- check 8 ---------------------------------------------------------------------------------

        [Fact]
        public void Two_steps_with_the_same_id_are_refused_naming_both_step_numbers()
        {
            var def = WorkflowParser.Parse(Steps(new[] { "id: prove" }, null, new[] { "id: prove" }));
            Assert.NotNull(def.Error);
            Assert.Contains("'prove'", def.Error);
            Assert.Contains("Step 1", def.Error);
            Assert.Contains("Step 3", def.Error);
        }

        // ---- check 9: an explicit id may not alias another step's positional address ------------------

        [Fact]
        public void A_positional_id_belonging_to_another_step_is_refused()
        {
            var def = WorkflowParser.Parse(Steps(null, null, null, null, new[] { "id: step-2" }));
            Assert.NotNull(def.Error);
            Assert.Contains("step-2", def.Error);
            Assert.Contains("Step 5", def.Error);
        }

        [Fact]
        public void A_step_may_declare_its_own_positional_id()
        {
            var def = WorkflowParser.Parse(Steps(null, null, null, null, new[] { "id: step-5" }));
            Assert.Null(def.Error);
            Assert.Equal("step-5", def.Steps[4].Id);
        }

        [Fact]
        public void An_explicit_id_may_not_collide_with_a_later_steps_positional_address()
        {
            // step 1 taking `step-3` would silently alias step 3, which has no fence and is therefore
            // addressed positionally. The shadow is the defect, not the spelling.
            var def = WorkflowParser.Parse(Steps(new[] { "id: step-3" }, null, null));
            Assert.NotNull(def.Error);
            Assert.Contains("step-3", def.Error);
        }

        // ---- the one-fence-per-step rule mirrors the existing one-gate-per-step rule ------------------

        [Fact]
        public void Two_step_fences_on_one_step_are_refused()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: Do", "",
                "```yaml step", "id: a", "```", "",
                "```yaml step", "id: b", "```", "",
                "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("more than one 'yaml step' block", def.Error);
        }

        [Fact]
        public void A_step_fence_is_not_part_of_the_instruction_text()
        {
            var def = WorkflowParser.Parse(Steps(new[] { "id: do-it" }));
            Assert.Null(def.Error);
            Assert.Equal("Body 1.", def.Steps[0].Instructions);
        }

        // ---- v1 is untouched -------------------------------------------------------------------------

        [Fact]
        public void A_v1_file_ignores_a_step_fence_entirely()
        {
            // v1 parses forever and its behaviour is byte-identical, so the fence is inert TEXT, exactly as
            // it is today. The silence is broken by a check_workflow warn (WorkflowV2CheckTests), not by a
            // parse-time behaviour change that would make a v1 file mean something new.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "title: T", "---", "",
                "## Step 1: Do", "",
                "```yaml step", "id: do-it", "```", "",
                "Body."));
            Assert.Null(def.Error);
            Assert.Equal("step-1", def.Steps[0].Id);
            Assert.Contains("id: do-it", def.Steps[0].Instructions);
        }
    }
}
