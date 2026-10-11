using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// m11/rule-clashes round 8. The promise, for every rule and every path at once: an accepted finding is never carried out, and an
    /// acceptance never reads another finding as accepted or lets it past the deploy gate. Proved by a matrix generated from the
    /// bundled rule set (RuleClashMatrix.cs), so a rule added later, or a path nobody thought of, is checked with no edit here.
    /// </summary>
    public sealed class RuleClashMatrixTests
    {
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task No_path_carries_out_an_accepted_finding_and_no_acceptance_lifts_a_finding_nobody_accepted()
        {
            var run = await RuleClashMatrix.GetRunAsync();
            Assert.True(run.Batches >= 3 && run.Acceptances >= 60, "the matrix must accept many rules (batches " + run.Batches + ", acceptances " + run.Acceptances + ")");
            Assert.True(run.Problems.Count == 0, run.Problems.Count + " problems:\n" + string.Join("\n", run.Problems.Distinct().Take(80)));
        }

        /// <summary>The default-tier witness of the matrix (lean suite): the same promise on one fixture, two declining rules, one batch of findings,
        /// through Run the plan. The full matrix (every rule, four fixtures, seven paths) is the sweep
        /// test above; the sample keeps a change to the accepted-finding handling from slipping past every pull request.</summary>
        [Fact]
        public async Task No_path_carries_out_an_accepted_finding_and_no_acceptance_lifts_a_finding_nobody_accepted_sample()
        {
            var run = await RuleClashMatrix.GetSampleRunAsync();
            Assert.True(run.Batches >= 1 && run.Acceptances >= 2, "the sample must accept some rules (batches " + run.Batches + ", acceptances " + run.Acceptances + ")");
            Assert.True(run.Problems.Count == 0, run.Problems.Count + " problems:\n" + string.Join("\n", run.Problems.Distinct().Take(80)));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_bundled_rule_is_exercised_or_excused_with_its_reason()
        {
            var run = await RuleClashMatrix.GetRunAsync();
            var unexplained = run.Rows.Where(r => r.Fixture == null && !r.Rule.Live && !r.Rule.Fallback && !RuleClashMatrix.NotExercised.ContainsKey(r.Rule.Key)).Select(r => r.Rule.Key).ToList();
            Assert.True(unexplained.Count == 0, "no fixture makes these fire and none is excused: " + string.Join(", ", unexplained));
            var stale = RuleClashMatrix.NotExercised.Keys.Where(k => run.Rows.Any(r => r.Rule.Key == k && r.Fixture != null)).ToList();
            Assert.True(stale.Count == 0, "excused but they fire now: " + string.Join(", ", stale));
        }

        /// <summary>The ops that carry out or queue what findings ask for, and the matrix path that drives each. An op that applies, makes or
        /// approves something and is on neither list fails here until somebody says which it is, so a new path cannot slip past the matrix.</summary>
        private static readonly Dictionary<string, string[]> Driven = new(StringComparer.Ordinal)
        {
            ["apply_plan"] = new[] { "change-plan" },
            ["apply_plan_items"] = new[] { "run-the-plan", "run-each-row" },
            ["model_plan_action"] = new[] { "run-each-row", "assistant-rows" },
            ["apply_safe_fixes"] = new[] { "safe-fixes", "bpa-fix-all" },
            ["make_model_ai_ready"] = new[] { "make-ai-ready" },
        };

        /// <summary>Ops that apply something the person or assistant wrote or named themselves, never what a finding asks for.</summary>
        private static readonly Dictionary<string, string> Explicit = new(StringComparer.Ordinal)
        {
            ["apply_fix"] = "one finding, named by the caller: an explicit request, like a line added to the plan by hand",
            ["apply_dax_script"] = "a script the caller wrote",
            ["apply_tmdl"] = "TMDL the caller wrote",
            ["apply_model_diff"] = "a diff the caller chose",
            ["apply_schema_update"] = "a source schema change the caller chose",
        };

        [Fact]
        public void Every_op_that_applies_or_approves_is_a_path_the_matrix_drives_or_an_explicit_request()
        {
            var ops = typeof(McpTools).Assembly.GetTypes().Where(t => t.IsAbstract && t.IsSealed)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(m => m.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute"))
                .Where(a => a != null)
                .Select(a => (string?)a!.GetType().GetProperty("Name")?.GetValue(a))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Where(n => n.StartsWith("apply_", StringComparison.Ordinal) || n.StartsWith("make_", StringComparison.Ordinal) || n == "model_plan_action")
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            Assert.True(ops.Count >= 8, "the tool list was not read (saw " + ops.Count + ")");
            var unclassified = ops.Where(n => !Driven.ContainsKey(n) && !Explicit.ContainsKey(n)).ToList();
            Assert.True(unclassified.Count == 0, "these ops apply or approve and the matrix does not say how it covers them: " + string.Join(", ", unclassified));
            var missing = Driven.Where(kv => !ops.Contains(kv.Key) || kv.Value.Any(p => !RuleClashMatrix.Paths.Contains(p))).Select(kv => kv.Key).ToList();
            Assert.True(missing.Count == 0, "a driven op is gone from the tool list or names a path the matrix does not drive: " + string.Join(", ", missing));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_committed_table_is_the_one_the_generator_makes()
        {
            var run = await RuleClashMatrix.GetRunAsync();
            var table = RuleClashMatrix.TableOf(run);
            var path = Path.Combine(RepoRoot(), "docs", "rule-clash-matrix.md");
            if (Environment.GetEnvironmentVariable("SEMANTICUS_MATRIX_WRITE") == "1") File.WriteAllText(path, table);
            Assert.True(File.Exists(path), "docs/rule-clash-matrix.md is missing; regenerate it (see the top of the table)");
            Assert.Equal(table.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Semanticus.sln"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("the repository root was not found");
        }
    }
}
