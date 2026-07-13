using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Parse-proof for the shipped stock workflow library (docs/pro-mode-spec.md §6). The seed *.md
    /// are copied beside the binary by the Engine csproj; if any file broke the strict
    /// <see cref="WorkflowParser"/> grammar it would surface with <c>Error</c> set and the funnel
    /// would ship a broken playbook. This pins the stock seeds parsing clean, each an enforced Pro
    /// funnel, the optimize-dax equivalence proof wired to the recorded-original probe, and every
    /// gate input carrying a question the agent can actually ask the user.
    /// </summary>
    public sealed class WorkflowSeedLibraryTests
    {
        // The lean launch set (chore/trim-workflow-library, 2026-07-08), plus the R6 blast-radius referee. The rest were CUT
        // (merged into a keeper or dropped) or DEFERRED — parked in Semanticus.Engine/workflows-parked/
        // (kept for IP, excluded from the csproj copy glob so they don't ship or parse here).
        private static readonly string[] Expected =
            { "add-relationship", "calendar-setup", "check-blast-radius", "deploy-to-production", "governed-rename",
              "import-table", "incremental-refresh-setup", "make-ai-ready", "model-hygiene-pass",
              "new-measure", "optimize-dax", "refactor-to-calculation-group", "secure-with-rls",
              "time-intelligence-variants", "verified-measure" };

        private static List<WorkflowDef> LoadStock()
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "workflows");
            Assert.True(Directory.Exists(dir), $"stock workflow dir was not copied beside the binary: {dir}");
            return WorkflowParser.LoadDirectory(dir, "stock");
        }

        [Fact]
        public void The_stock_workflows_ship_and_parse_clean()
        {
            var defs = LoadStock();
            Assert.Equal(Expected, defs.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            foreach (var d in defs)
                Assert.True(d.Error == null, $"'{d.Name}' failed to parse: {d.Error}");
        }

        [Fact]
        public void Every_stock_workflow_is_an_enforced_pro_funnel()
        {
            foreach (var d in LoadStock())
                Assert.True(d.HasEnforcedGate(), $"'{d.Name}' has no enforced gate — it would run free, defeating the funnel.");
        }

        [Fact]
        public void Optimize_dax_hard_gates_equivalence_against_the_recorded_original()
        {
            var opt = LoadStock().Single(d => d.Name == "optimize-dax");
            Assert.Null(opt.Error);
            var gate = opt.Steps.Select(s => s.Gate).FirstOrDefault(g =>
                g != null && g.Strictness == "hard"
                && g.Verify.Any(v => v.Kind == "dax_equivalence" && v.Probe == "originalDax"));
            Assert.NotNull(gate);
        }

        [Fact]
        public void Every_gate_input_gives_the_agent_a_question_to_ask()
        {
            foreach (var d in LoadStock())
                foreach (var step in d.Steps)
                    foreach (var input in step.Gate?.Inputs ?? Array.Empty<GateInput>())
                        Assert.False(string.IsNullOrWhiteSpace(input.Question),
                            $"'{d.Name}' {step.Id} input '{input.Name}' has no question for the agent to ask.");
        }
    }
}
