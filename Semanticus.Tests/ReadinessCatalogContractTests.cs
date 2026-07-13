using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Semanticus.Analysis;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ReadinessCatalogContractTests
    {
        private static string FindCatalog()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestData", "ai-readiness-catalog.json");
            return File.Exists(path) ? path : throw new FileNotFoundException("ai-readiness-catalog.json was not copied beside the test assembly", path);
        }

        [Fact]
        public void Covered_ledger_counts_and_rule_ids_match_the_shipped_ruleset()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FindCatalog()));
            var root = doc.RootElement;
            var counts = root.GetProperty("counts");
            var covered = root.GetProperty("covered").EnumerateArray().ToArray();

            Assert.Equal(counts.GetProperty("covered").GetInt32(), covered.Length);
            Assert.Equal(counts.GetProperty("rawRequirements").GetInt32(),
                counts.GetProperty("covered").GetInt32()
                + counts.GetProperty("partial").GetInt32()
                + counts.GetProperty("missing").GetInt32());

            var builtIns = ReadinessRuleSet.Default().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var titles = new HashSet<string>(StringComparer.Ordinal);
            var idPattern = new Regex(@"\b[A-Z][A-Z0-9]+(?:-[A-Z0-9]+)+\b");
            foreach (var row in covered)
            {
                var title = row.GetProperty("title").GetString();
                Assert.True(titles.Add(title), $"duplicate covered title: {title}");

                var mapped = idPattern.Matches(row.GetProperty("ourRuleId").GetString() ?? "")
                    .Select(m => m.Value).ToArray();
                Assert.NotEmpty(mapped);
                Assert.Contains(mapped, builtIns.Contains);
            }
        }

        [Fact]
        public void Launch_cutoff_matches_the_current_rules_and_names_only_honest_residuals()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FindCatalog()));
            var root = doc.RootElement;
            var audit = root.GetProperty("launchReadiness");
            Assert.Equal("enough-for-1.0", audit.GetProperty("verdict").GetString());

            var offline = ReadinessRuleSet.Default();
            var live = ReadinessRuleSet.LiveRules(new ReadinessLiveStats());
            Assert.Equal(audit.GetProperty("offlineBuiltInRules").GetInt32(), offline.Count);
            Assert.Equal(audit.GetProperty("liveBuiltInRules").GetInt32(), live.Count);

            var builtIns = offline.Concat(live).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in audit.GetProperty("closedCriticRuleIds").EnumerateArray().Select(x => x.GetString()))
                Assert.Contains(id, builtIns);

            var allowedResiduals = new HashSet<string>(StringComparer.Ordinal)
            {
                "deferred-schema", "partial-schema", "out-of-band", "report-boundary", "judgment"
            };
            var residuals = root.GetProperty("criticMissed").EnumerateArray().ToArray();
            Assert.NotEmpty(residuals);
            Assert.All(residuals, r => Assert.Contains(r.GetProperty("status").GetString(), allowedResiduals));
        }
    }
}
