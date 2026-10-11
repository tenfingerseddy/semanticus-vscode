// W4 (lanes.md, plan-heart build): the describe-it test on the four starters W3 left waiting for Jev drafting (L5b).
//
// For each starter, a person describes the same work in plain words, one clause per starter step. The product's own
// Describe path (describe_workflow_steps, Jev drafting on, the real vendor client) turns the words into steps. The
// starter is parkable only when those drafted steps would run what the starter runs: every change op group the starter
// gates on has a drafted step holding one of its ops, no drafted step makes a change the starter never makes, and every
// check kind the starter verifies is carried by a drafted step that runs the starter's own work (DescribedStep.Checks).
// A starter that does not match stays, with the reason written down. That is stricter than the plan's words (contract
// 21.2 "park the starter only if the draft carries its checks"; lanes.md W4 "each starter whose drafted checks match"),
// which read literally would park a starter on a wrong draft whose checks came from a step the starter never runs
// (m11/completeness D6). The strict side is the safe one: a starter parked in error is lost work.
//
// The live fact runs ONLY when JEV_REPLAY_LIVE is exactly "1"; otherwise it is reported skipped, never passed. The key is
// read from ~/.config/typesafe/env into this process only and is never printed or written. Results go OUTSIDE the
// repository (~/.cache/jev-replay-compose/starters.json, or JEV_REPLAY_OUT).
//
//   JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevStarterDescribeIt"

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevStarterDescribeIt
    {
        /// <summary>A starter, the words a person would use for it (one clause per starter step), what it changes (one op
        /// group per changing step: a drafted step must hold one op of each) and the check kinds it verifies.</summary>
        internal sealed record Starter(string Name, string File, string Words, string[][] Changes, string[] Checks);

        // Reads (run_dax, impact_assessment, analyze_reports, list_*, get_*) are left out of Changes: a draft runs every
        // read as your assistant's, so a read never decides whether the words can stand in for the starter.
        internal static readonly Starter[] Starters =
        {
            new("incremental-refresh-setup", "workflows/incremental-refresh-setup.md",
                "choose the Sales fact with a rolling window, then add the RangeStart parameter plus the incremental refresh policy, then refresh the partitions to check one window against a control measure, then save the model",
                new[] { new[] { "create_named_expression", "set_incremental_refresh_policy" }, new[] { "save_model" } },
                new[] { "dax_probe" }),
            new("reconcile-saved-tests-sql", "workflows/reconcile-saved-tests-sql.md",
                "choose the saved SQL test pack, then run the saved tests against SQL, then export the test report",
                new[] { new[] { "run_tests" }, new[] { "export_test_report" } },
                Array.Empty<string>()),
            new("refactor-to-calculation-group", "workflows/refactor-to-calculation-group.md",
                "choose the repeated time variant measures, then capture a before matrix with DAX, then build a calculation group with one item per variant, then compare the outputs against the before matrix, then delete the replaced measures before saving the model",
                new[] { new[] { "create_calculation_group", "create_calculation_item" }, new[] { "delete_object" }, new[] { "save_model" } },
                new[] { "bpa_clean" }),
            new("metric-certification", "workflow-templates/metric-certification.md",
                "confirm the KPI dictionary still holds, then check every KPI against its owner's figure, then prove the anchor number with a DAX probe, then stamp the certified measures before saving the model",
                new[] { new[] { "set_description", "set_property", "set_properties" }, new[] { "save_model" } },
                new[] { "dax_probe", "bpa_clean" }),
        };

        /// <summary>The ops a draft can make a change with: the composer's change templates. Reads and the steps after
        /// the change (refresh, run the checks, publish, save) are not model changes of the draft's own.</summary>
        private static readonly HashSet<string> ChangeOps = DraftMenu.All.Select(t => t.Op).ToHashSet(StringComparer.Ordinal);

        /// <summary>The verdict for one starter's drafted steps: null when they match, else the first reason they do not.
        /// A drafted step that changes the model with an op the starter never runs is a wrong draft, and its checks do not
        /// count towards the starter's: "confirm the KPI dictionary" drafted as Add a measure carries dax_probe and
        /// bpa_clean, and those are not metric-certification's checks being run.</summary>
        internal static string Mismatch(Starter s, IReadOnlyList<DescribedStep> steps)
        {
            var starterOps = s.Changes.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
            var foreign = steps.FirstOrDefault(x => (x.Ops ?? Array.Empty<string>()).Any(op => ChangeOps.Contains(op) && !starterOps.Contains(op)));
            if (foreign != null)
                return "a drafted step runs " + string.Join(" and ", foreign.Ops.Where(op => ChangeOps.Contains(op) && !starterOps.Contains(op)))
                    + ", a change the starter never makes";
            var ops = steps.SelectMany(x => x.Ops ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
            var checks = steps.SelectMany(x => x.Checks ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var group in s.Changes)
                if (!group.Any(ops.Contains)) return "no drafted step runs " + string.Join(" or ", group);
            foreach (var kind in s.Checks)
                if (!checks.Contains(kind)) return "no drafted step carries the " + kind + " check";
            return null;
        }

        private static string Repo()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Semanticus.sln"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
        }

        /// <summary>Not live: the table above says what each starter file really gates on and verifies, and each set of
        /// words cuts into one step per starter step, so the live verdict compares like with like.</summary>
        [Fact]
        public void Each_starter_row_matches_its_file_and_its_words_cut_into_its_steps()
        {
            foreach (var s in Starters)
            {
                var text = File.ReadAllText(Path.Combine(Repo(), "Semanticus.Engine", s.File));
                var stepCount = Regex.Matches(text, @"^## Step \d+", RegexOptions.Multiline).Count;
                Assert.True(DescribedSteps.From(s.Words).Steps.Length == stepCount, s.Name + ": the words do not cut into " + stepCount + " steps");
                var verified = Regex.Matches(text, @"^\s+- kind: (\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct().OrderBy(k => k);
                Assert.Equal(verified, s.Checks.OrderBy(k => k));
                // Every change op the row names is one the starter's gates (or, for the template, its text) name.
                foreach (var op in s.Changes.SelectMany(g => g))
                    if (!Regex.IsMatch(text, @"\b" + op + @"\b") && op != "set_property" && op != "set_properties" && op != "set_description")
                        Assert.Fail(s.Name + " never names " + op);
            }
        }

        [Fact]
        public void The_verdict_needs_every_change_group_and_every_check()
        {
            var s = Starters.Single(x => x.Name == "refactor-to-calculation-group");
            DescribedStep Step(string op, params string[] checks) => new DescribedStep { Ops = new[] { op }, Checks = checks };
            Assert.Null(Mismatch(s, new[] { Step("create_calculation_group"), Step("delete_object", "bpa_clean"), Step("save_model") }));
            Assert.Contains("delete_object", Mismatch(s, new[] { Step("create_calculation_group", "bpa_clean"), Step("save_model") }));
            Assert.Contains("bpa_clean", Mismatch(s, new[] { Step("create_calculation_group"), Step("delete_object"), Step("save_model") }));
            // Every group and check present, but one more step adds a measure the starter never adds.
            Assert.Contains("create_measure", Mismatch(s, new[] { Step("create_measure", "bpa_clean"), Step("create_calculation_group"), Step("delete_object", "bpa_clean"), Step("save_model") }));
        }

        /// <summary>The verdict docs/redesign/w4-describe-it.md records: every pending starter stays. The live fact fails
        /// when a run would park one, so the doc and the starters cannot drift from what Jev really drafts.</summary>
        internal static readonly string[] StayAfterW4 = { "incremental-refresh-setup", "reconcile-saved-tests-sql", "refactor-to-calculation-group", "metric-certification" };

        [JevReplayFact]
        public async Task Describe_each_pending_starter_through_the_product()
        {
            var repo = Repo();
            var outDir = Environment.GetEnvironmentVariable("JEV_REPLAY_OUT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "jev-replay-compose");
            if (Path.GetFullPath(outDir).StartsWith(Path.GetFullPath(repo), StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to write results inside the repository.");
            Directory.CreateDirectory(outDir);

            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            foreach (var t in new[] { "Sales", "Product", "Customer", "Date" })
                await f.Engine.CreateTableAsync(t, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "human");
            await f.SaveAsAsync("Contoso.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            var budget = new DecisionBudget(500_000);
            using var jev = new JevDecisionOracle(JevKey(), budget);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, budget));

            var rows = new List<object>();
            var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in Starters)
            {
                var d = await f.Engine.DescribeWorkflowStepsAsync(s.Words);
                verdicts[s.Name] = Mismatch(s, d.Steps);
                rows.Add(new
                {
                    starter = s.Name,
                    jevAsked = d.JevAsked,
                    jevNote = d.JevNote,
                    steps = d.Steps.Select(x => new { x.Words, x.Ops, x.ChosenBy, x.Checks }).ToArray(),
                    mismatch = Mismatch(s, d.Steps),
                });
            }
            File.WriteAllText(Path.Combine(outDir, "starters.json"), JsonSerializer.Serialize(new
            {
                ranUtc = DateTime.UtcNow.ToString("o"),
                requests = budget.Requests,
                inputTokens = budget.UsedInputTokens,
                rows,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Equal(Starters.Length, rows.Count);
            // Pin the recorded verdict: a starter the doc says stays must still not match. A run that parks one fails here
            // until the doc, the starter and this list are changed together.
            foreach (var name in StayAfterW4)
                Assert.True(verdicts[name] != null, name + " now matches its words; docs/redesign/w4-describe-it.md says it stays");
        }

        private static string JevKey()
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "typesafe", "env");
            var m = Regex.Match(File.ReadAllText(file), @"^\s*(?:export\s+)?[A-Z0-9_]*(?:KEY|TOKEN)[A-Z0-9_]*\s*=\s*[""']?([^""'\s]+)", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidOperationException("No key in the key file.");
            return m.Groups[1].Value;
        }
    }
}
