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

            Assert.Equal(199, counts.GetProperty("rawRequirements").GetInt32());
            Assert.Equal(194, counts.GetProperty("distinct").GetInt32());
            Assert.Equal(192, counts.GetProperty("confirmed").GetInt32());
            Assert.Equal(62, counts.GetProperty("covered").GetInt32());
            Assert.Equal(50, counts.GetProperty("partial").GetInt32());
            Assert.Equal(87, counts.GetProperty("missing").GetInt32());
            Assert.Equal(138, counts.GetProperty("residualNonGoals").GetInt32());
            Assert.Equal(counts.GetProperty("covered").GetInt32(), covered.Length);
            Assert.Equal(counts.GetProperty("rawRequirements").GetInt32(),
                counts.GetProperty("covered").GetInt32()
                + counts.GetProperty("partial").GetInt32()
                + counts.GetProperty("missing").GetInt32());

            var builtIns = ReadinessRuleSet.Default()
                .Concat(ReadinessRuleSet.LiveRules(new ReadinessLiveStats()))
                .Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
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

            var byTitle = covered.ToDictionary(
                row => row.GetProperty("title").GetString()!,
                row => row.GetProperty("ourRuleId").GetString()!,
                StringComparer.Ordinal);
            var gapStatusByTitle = root.GetProperty("gaps").EnumerateArray().ToDictionary(
                row => row.GetProperty("title").GetString()!,
                row => row.GetProperty("status").GetString()!,
                StringComparer.Ordinal);
            var gapRulesByTitle = root.GetProperty("gaps").EnumerateArray().ToDictionary(
                row => row.GetProperty("title").GetString()!,
                row => row.GetProperty("ourRuleId").GetString()!,
                StringComparer.Ordinal);
            foreach (var title in byTitle.Keys.Where(gapStatusByTitle.ContainsKey))
                Assert.Equal("covered", gapStatusByTitle[title]);
            Assert.Equal("SYN-COLLIDE", byTitle["Make the primary synonym unique to avoid ambiguity"]);
            Assert.Equal("FMT-MEASURE, FMT-COLUMN", byTitle["Format strings are used as grounding metadata"]);
            Assert.Equal("CAT-GEO, CAT-URL", byTitle["Data category is used as grounding metadata"]);

            var batch2 = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DirectQuery / Direct Lake models require manual Q&A enablement"] = "DAC-QNA-STORAGE, DAC-QNA-DISABLED",
                ["Calculation items are absent from metadata; use group description"] = "DAC-CALC-GROUP, DESC-CALCGROUP-ITEMS",
                ["Use calculation group column descriptions to list calculation items"] = "DAC-CALC-GROUP, DESC-CALCGROUP-ITEMS",
                ["Apply correct and consistent data types"] = "FMT-DATATYPE",
                ["Fix incorrect data types (dates/numbers imported as strings)"] = "FMT-DATATYPE",
                ["Description grounding is truncated at 200 characters"] = "DESC-LONG, DESC-LONG-OBJECT",
                ["Descriptions truncated after first 200 characters for Copilot grounding"] = "DESC-LONG, DESC-LONG-OBJECT",
                ["Minimize data source scope; limit to 25 or fewer tables"] = "LIMIT-DATAAGENT-TABLES",
                ["Set Sort By Column so natural-language sorting is logical"] = "FMT-SORTBY",
            };
            foreach (var expected in batch2)
                Assert.Equal(expected.Value, byTitle[expected.Key]);

            var batch3 = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Add missing relationships so tables can be joined"] = "REL-DISCONNECTED",
                ["Q&A index hard limit: 5 million unique values"] = "SCALE-QNA-INDEX",
                ["Q&A setup (linguistic teaching) only for Import and DirectQuery"] = "MODE-DIRECTLAKE-QNA, CFG-PREP-MODE",
                ["Implicit measures and wrong default summarization degrade results"] = "DAC-IMPLICIT-MEASURE, FMT-SUMMARIZE, SUMMARIZE-DIMENSION",
                ["Use explicit measures and correct default summarization, not implicit measures"] = "DAC-IMPLICIT-MEASURE, FMT-SUMMARIZE, SUMMARIZE-DIMENSION",
            };
            foreach (var expected in batch3)
                Assert.Equal(expected.Value, byTitle[expected.Key]);

            var batch4 = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Disambiguate multiple date fields"] = "DATE-AMBIGUOUS",
                ["Enable Power BI Q&A on the model (prerequisite for Prep data for AI)"] = "DAC-QNA-DISABLED",
                ["Q&A must be enabled for Copilot data questions"] = "DAC-QNA-DISABLED",
                ["Prep for AI requires Q&A enabled (preview gate)"] = "DAC-QNA-DISABLED",
                ["Enable Power BI Q&A as a prerequisite for all Prep-for-AI features"] = "DAC-QNA-DISABLED",
                ["Linguistic metadata feeds Copilot - the model linguistic schema is grounding data"] = "SYN-SCHEMA, SYN-FIELD, SYN-TABLE",
                ["Add synonyms to tables and columns"] = "SYN-FIELD, SYN-TABLE",
                ["Add AI Instructions to encode business vocabulary and synonyms"] = "DAC-AI-INSTRUCTIONS, DAC-GLOSSARY-GAP",
                ["Add AI instructions in Prep for AI (not at data-agent level for semantic models)"] = "DAC-AI-INSTRUCTIONS",
                ["Create explicit measures and set correct default summarization; avoid implicit measures"] = "DAC-IMPLICIT-MEASURE, FMT-SUMMARIZE, SUMMARIZE-DIMENSION",
            };
            foreach (var expected in batch4)
                Assert.Equal(expected.Value, byTitle[expected.Key]);

            var batch5 = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Human-readable, business-friendly names for tables, columns, and measures"] = "NAME-MEASURE, NAME-COLUMN, NAME-TABLE",
                ["Unambiguous, self-explanatory column names (no bare IDs/codes)"] = "NAME-COLUMN, NAME-COLUMN-ID",
                ["Define clear relationships with correct cardinality and active/inactive state"] = "REL-BIDI, REL-M2M, REL-INACTIVE",
                ["Use a star schema with clear fact and dimension tables"] = "REL-BIDI, REL-M2M, REL-SNOWFLAKE, REL-DISCONNECTED",
                ["Poor star-schema design produces poor Copilot results"] = "REL-BIDI, REL-M2M, REL-SNOWFLAKE, REL-DISCONNECTED",
                ["Establish logical hierarchies for drill-down"] = "REL-HIERARCHY-MISSING, REL-HIERARCHY-SINGLE-LEVEL",
                ["Follow star-schema design for natural-language consumption"] = "REL-BIDI, REL-M2M, REL-SNOWFLAKE, REL-DISCONNECTED",
                ["Use a star schema, not flat/denormalized/pivoted tables"] = "REL-BIDI, REL-M2M, REL-SNOWFLAKE, REL-DISCONNECTED",
                ["Non-star-schema / flat denormalized models hurt DAX generation"] = "REL-BIDI, REL-M2M, REL-SNOWFLAKE, REL-DISCONNECTED",
                ["Use a star schema; avoid flat/denormalized/pivoted tables"] = "REL-BIDI, REL-M2M, REL-SNOWFLAKE, REL-DISCONNECTED",
            };
            foreach (var expected in batch5)
            {
                Assert.Equal(expected.Value, gapRulesByTitle[expected.Key]);
                foreach (Match id in idPattern.Matches(expected.Value)) Assert.Contains(id.Value, builtIns);
            }

            var batch6 = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Reduce model complexity and remove bloat"] = "LIMIT-SCALE, SCALE-HICARD-COLUMN, REL-DISCONNECTED, DAC-FIELD-PARAM-COMPLEXITY, MEAS-DUP-EXPR",
                ["Optimize the model for performance / use BPA and Memory Analyzer"] = "FMT-SUMMARIZE, SCALE-HICARD-COLUMN, BP-DAX-IFERROR, BP-DAX-SUMMARIZE-EXT, BP-DAX-BARE-TABLE-FILTER, BP-DAX-VAR-ALIAS",
                ["Limit model complexity for reliable natural-language answers"] = "LIMIT-SCALE, REL-DISCONNECTED, DAC-FIELD-PARAM-COMPLEXITY",
                ["Optimize the semantic model for performance / remove bloat"] = "LIMIT-SCALE, SCALE-HICARD-COLUMN, FMT-SUMMARIZE, BP-DAX-IFERROR, BP-DAX-SUMMARIZE-EXT, BP-DAX-BARE-TABLE-FILTER, BP-DAX-VAR-ALIAS",
                ["Model bloat (excess tables/columns/measures) degrades accuracy"] = "LIMIT-SCALE, SCALE-HICARD-COLUMN, REL-DISCONNECTED, DAC-FIELD-PARAM-COMPLEXITY, MEAS-DUP-EXPR",
                ["High-cardinality columns and inefficient DAX flagged as model problems"] = "SCALE-HICARD-COLUMN, BP-DAX-IFERROR, BP-DAX-SUMMARIZE-EXT, BP-DAX-BARE-TABLE-FILTER, BP-DAX-VAR-ALIAS",
                ["Model complexity (field params, disconnected tables, currency conversion) reduces reliability"] = "LIMIT-SCALE, REL-DISCONNECTED, DAC-FIELD-PARAM-COMPLEXITY",
                ["Optimize model performance / remove bloat before AI use"] = "LIMIT-SCALE, SCALE-HICARD-COLUMN, REL-DISCONNECTED, DAC-FIELD-PARAM-COMPLEXITY, MEAS-DUP-EXPR, FMT-SUMMARIZE, BP-DAX-IFERROR, BP-DAX-SUMMARIZE-EXT, BP-DAX-BARE-TABLE-FILTER, BP-DAX-VAR-ALIAS",
            };
            foreach (var expected in batch6)
            {
                Assert.Equal(expected.Value, gapRulesByTitle[expected.Key]);
                foreach (Match id in idPattern.Matches(expected.Value)) Assert.Contains(id.Value, builtIns);
            }
            Assert.Equal(batch6["High-cardinality columns and inefficient DAX flagged as model problems"],
                byTitle["High-cardinality columns and inefficient DAX flagged as model problems"]);
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

            var gapTitles = root.GetProperty("gaps").EnumerateArray()
                .Select(row => row.GetProperty("title").GetString())
                .ToHashSet(StringComparer.Ordinal);
            var rejectedTitles = root.GetProperty("rejected").EnumerateArray()
                .Select(row => row.GetProperty("title").GetString())
                .ToHashSet(StringComparer.Ordinal);
            var coveredTitles = root.GetProperty("covered").EnumerateArray()
                .Select(row => row.GetProperty("title").GetString())
                .ToHashSet(StringComparer.Ordinal);
            var buckets = root.GetProperty("residualBuckets");
            Assert.True(allowedResiduals.SetEquals(buckets.EnumerateObject().Select(p => p.Name)));

            var marked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bucket in buckets.EnumerateObject())
            foreach (var title in bucket.Value.EnumerateArray().Select(x => x.GetString()))
            {
                Assert.NotNull(title);
                Assert.True(marked.Add(title!), $"residual row assigned to more than one bucket: {title}");
                Assert.Contains(title, gapTitles);
                Assert.DoesNotContain(title, rejectedTitles);
                Assert.DoesNotContain(title, coveredTitles);
            }
            Assert.Equal(root.GetProperty("counts").GetProperty("residualNonGoals").GetInt32(), marked.Count);

            var batch6Residuals = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Use Teach Q&A to define unrecognized noun synonyms"] = "deferred-schema",
                ["Linguistic modeling / synonyms required for good Copilot results"] = "deferred-schema",
                ["Remove redundant fields (e.g. extra date/FK columns) from Q&A"] = "partial-schema",
                ["Optimize the model for performance / use BPA and Memory Analyzer"] = "out-of-band",
                ["Verify the generated DAX in each test response"] = "out-of-band",
                ["Verify and inspect generated DAX during testing"] = "out-of-band",
                ["Mark a date table so Copilot filters time correctly"] = "judgment",
                ["Reduce model complexity and remove bloat"] = "judgment",
                ["Set Summarization to Don't summarize for non-additive numeric columns"] = "judgment",
                ["Set a Data Category for each date and geography column"] = "judgment",
                ["Limit model complexity for reliable natural-language answers"] = "judgment",
                ["Optimize the semantic model for performance / remove bloat"] = "judgment",
                ["Model bloat (excess tables/columns/measures) degrades accuracy"] = "judgment",
                ["Model complexity (field params, disconnected tables, currency conversion) reduces reliability"] = "judgment",
                ["Comments and quoted strings in DAX are not used by Copilot"] = "judgment",
                ["Optimize model performance / remove bloat before AI use"] = "judgment",
            };
            foreach (var expected in batch6Residuals)
            {
                var actualBucket = buckets.EnumerateObject().Single(bucket =>
                    bucket.Value.EnumerateArray().Any(title => title.GetString() == expected.Key)).Name;
                Assert.Equal(expected.Value, actualBucket);
            }

            var actionable = root.GetProperty("gaps").EnumerateArray()
                .Where(row => row.GetProperty("status").GetString() != "covered")
                .Select(row => row.GetProperty("title").GetString()!)
                .Where(title => !marked.Contains(title) && !rejectedTitles.Contains(title))
                .OrderBy(title => title, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[]
            {
                "Missing values cause Copilot to fabricate / hallucinate",
                "Q&A indexes only text values under 100 characters",
            }, actionable);
        }
    }
}
