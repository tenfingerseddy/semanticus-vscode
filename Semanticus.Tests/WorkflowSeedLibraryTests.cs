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
        //
        // Contract 21.2 (lane W3, slice 11): the library is now the six starters' workflows plus the three that wait on
        // the describe-it test. The seven parked starters were frozen into workflows-compat/<ParkRelease>/ and moved
        // to workflows-parked/, so a pinned binding still resolves while the shelf no longer offers them.
        private static readonly string[] Expected =
            { "check-blast-radius", "deploy-to-production", "governed-rename",
              "incremental-refresh-setup", "optimize-dax", "reconcile-saved-tests-sql",
              "refactor-to-calculation-group", "secure-with-rls", "verified-measure" };

        /// <summary>The seven starters parked when the composer shipped (contract 21.2), and the compatibility folder
        /// their last shipped bytes were frozen into.</summary>
        internal static readonly string[] Parked =
            { "add-relationship", "calendar-setup", "import-table", "make-ai-ready", "model-hygiene-pass",
              "new-measure", "time-intelligence-variants" };
        internal const string ParkRelease = "v1.2.2";

        internal static string ParkedFreeze(string name) =>
            Path.Combine(AppContext.BaseDirectory, "workflows-compat", ParkRelease, name + ".md");

        /// <summary>Contract 21.2: a test that names a parked starter loads it from its compat freeze. This writes the
        /// frozen bytes as a project copy in <paramref name="workspace"/>, so the workflow resolves (as "user") exactly
        /// as it ran when it shipped as stock. Returns the project file's path.</summary>
        internal static string LoadParkedAsProjectCopy(string workspace, string name)
        {
            var dir = Path.Combine(workspace, ".semanticus", "workflows");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, name + ".md");
            File.Copy(ParkedFreeze(name), file, overwrite: true);
            return file;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

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

        [Fact]
        public void Every_parked_starter_is_off_the_shelf_and_resolves_from_its_compat_freeze()
        {
            var stock = Path.Combine(AppContext.BaseDirectory, "workflows");
            foreach (var name in Parked)
            {
                Assert.False(File.Exists(Path.Combine(stock, name + ".md")), $"'{name}' is parked but still ships on the stock shelf");
                var frozen = ParkedFreeze(name);
                Assert.True(File.Exists(frozen), $"'{name}' has no frozen copy at {frozen}, so a binding pinned to it cannot resolve");
                var def = WorkflowParser.ParseFile(frozen, "stock");
                Assert.True(def.Error == null, $"the frozen '{name}' failed to parse: {def.Error}");
                Assert.Equal(name, def.Name);
                // The freeze is the parked file's bytes exactly; workflows-parked keeps the IP copy that does not ship.
                var parked = Path.Combine(RepoRoot(), "Semanticus.Engine", "workflows-parked", name + ".md");
                Assert.True(File.Exists(parked), $"'{name}' was not moved to workflows-parked/");
                Assert.Equal(File.ReadAllBytes(parked), File.ReadAllBytes(frozen));
            }
        }

        // D-095: step 1 help says the date column and fiscal start may be left blank. Those inputs must be optional.
        // calendar-setup is parked (contract 21.2); its frozen copy is what a pinned binding still runs.
        [Fact]
        public void Calendar_setup_step1_optional_fields_match_the_help()
        {
            var cal = WorkflowParser.ParseFile(ParkedFreeze("calendar-setup"), "stock");
            Assert.Null(cal.Error);
            var step1 = cal.Steps[0];
            var byName = step1.Gate.Inputs.ToDictionary(i => i.Name, StringComparer.Ordinal);
            Assert.Equal("required", byName["template"].Required);
            Assert.Equal("optional", byName["targetTable"].Required);
            Assert.Equal("optional", byName["dateColumn"].Required);
            Assert.Equal("optional", byName["fiscalStart"].Required);
            Assert.Contains("leave blank", byName["dateColumn"].Question, StringComparison.OrdinalIgnoreCase);
        }
    }
}
