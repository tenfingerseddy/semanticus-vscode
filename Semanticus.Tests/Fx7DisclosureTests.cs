using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests;

public sealed class Fx7DisclosureTests
{
    private static string Read(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Semanticus.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, path)).Replace("\r\n", "\n");
    }

    private static string Card(string source, string name)
    {
        var match = Regex.Match(source, @"const " + name + @" = \{([\s\S]*?)\n\};");
        Assert.True(match.Success, "Missing disclosure: " + name);
        return match.Groups[1].Value;
    }

    private static string Description(MethodInfo method)
        => method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

    [Fact]
    public void Every_disclosure_names_sent_fields_and_never_denies_fields_covered_by_its_grant()
    {
        var input = new TotalsInput("Sales", "Adds sales.", "SUM(Sales[Amount])");
        var snapshots = new[] {
            (Point: DescriptionMatchQuestions.Point, State: DescriptionMatchQuestions.Snapshot(new(input.Description, input.Formula, input.Name))),
            (Point: TotalsQuestions.TypePoint, State: (IReadOnlyDictionary<string, object>)TotalsQuestions.TypeSnapshot(input)),
            (Point: TotalsQuestions.FitPoint, State: (IReadOnlyDictionary<string, object>)TotalsQuestions.FitSnapshot(input,
                new("Customer[Name]", new[] { new TotalProbeRow("A customer", 100) }, 100), "sum_of_rows"))
        };
        var words = new Dictionary<string, string> {
            ["name"] = "name", ["description"] = "Description", ["formula"] = "formula",
            ["expected_total_rule"] = "expected total rule", ["grouped_by"] = "grouping column",
            ["facts"] = "calculated facts", ["rows"] = "probe rows", ["total"] = "total",
            ["member"] = "member name", ["value"] = "measure value"
        };
        var sent = new HashSet<string>();
        foreach (var (point, state) in snapshots)
        {
            foreach (var key in state.Keys)
            {
                Assert.Contains(point + ":" + key, DecisionRegistry.DeclaredFields);
                if (key != "probe") sent.Add(key);
            }
        }
        var fit = snapshots.Single(s => s.Point == TotalsQuestions.FitPoint);
        using var wire = JsonDocument.Parse(JevDecisionOracle.BuildRelayBody(
            new DecisionRequest(fit.Point, 1, fit.State, TotalsQuestions.FitQuestions), JsonSerializer.Serialize(fit.State), "fx7"));
        var probe = wire.RootElement.GetProperty("snapshot").GetProperty("probe");
        sent.UnionWith(probe.EnumerateObject().Select(p => p.Name));
        sent.UnionWith(probe.GetProperty("rows")[0].EnumerateObject().Select(p => p.Name));
        Assert.All(sent, key => Assert.True(words.ContainsKey(key), "Name the new wire field in the disclosures: " + key));

        var consent = Read("Semanticus.VSCode/webview/src/consent.tsx");
        var model = Card(consent, "CARD");
        var start = Card(consent, "START_CARD");
        // The start yes grants every field. Its model disclosure is rendered from CARD.goes.
        Assert.Contains("CARD.goes.map", consent[consent.IndexOf("consent-start-model", StringComparison.Ordinal)..]);
        var surfaces = new Dictionary<string, string> {
            ["Help"] = Read("Semanticus.VSCode/webview/src/help.tsx"),
            ["model consent card"] = model,
            ["start plan card"] = start + "\n" + model,
            ["README"] = Read("README.md"),
            ["extension README"] = Read("Semanticus.VSCode/README.md"),
            ["assistant guide source"] = Read(".claude/skills/semanticus/SKILL.md"),
            ["shipped assistant guide"] = Read("Semanticus.VSCode/assistant-pack/plugins/semanticus/skills/semanticus/SKILL.md")
        };
        foreach (var method in new[] { nameof(McpTools.GrantStartDisclosureOp), nameof(McpTools.SetConsent) })
            surfaces[method] = Description(typeof(McpTools).GetMethod(method)!);
        foreach (var mode in new[] { "none", "sampled", "reading", "refused" })
        {
            var manifest = new StructureManifest();
            manifest.Tables.Add(new ManifestTable { Name = "Sales", SourceName = "dbo.Sales", Include = true });
            var doc = new StartPlanDoc { SampleFacts = mode != "none" };
            if (mode == "reading") doc.Sample.State = StartSampleStatus.Reading;
            if (mode == "refused") doc.SampleRefused.Add("dbo.Sales");
            surfaces["start plan summary (" + mode + ")"] = StartPlanViewBuilder.GrantScopeWords(doc, manifest);
        }

        var errors = new List<string>();
        foreach (var (label, text) in surfaces)
        {
            foreach (var key in sent)
                if (!text.Contains(words[key], StringComparison.OrdinalIgnoreCase))
                    errors.Add(label + " does not name " + words[key]);
        }
        // Include the point-specific MCP descriptions, without requiring them to describe unrelated points.
        foreach (var method in new[] { nameof(McpTools.GetConsent), nameof(McpTools.GetDecisionPoints),
            nameof(McpTools.ProposeMeasureDefaults), nameof(McpTools.GetMeasureDescription), nameof(McpTools.GetMeasureMarks) })
            surfaces["MCP " + method] = Description(typeof(McpTools).GetMethod(method)!);
        foreach (var (point, _) in snapshots)
        {
            Assert.True(DecisionRegistry.TryGet(point, out var definition));
            surfaces["model decision card " + point] = definition.Purpose;
        }

        foreach (var (label, text) in surfaces)
        {
            // Source rows and rows outside the chosen probes are valid exclusions. A generic row/value exclusion is not.
            var copy = Regex.Replace(text, @"(?i)(?:source table rows themselves|source sample rows|table rows outside the chosen totals probes)", "local source data");
            copy = Regex.Replace(copy, @"never: \[([^\]]*)\]", m => "never sends " + m.Groups[1].Value);
            foreach (var clause in Regex.Split(copy, @"[.!?;\n]"))
            {
                if (!Regex.IsMatch(clause, @"(?i)\bnever\s+(?:(?:the|any|a|your)\s+)?(?:rows|values?|formulas?|names?|descriptions?)\b|\bnever\s+(?:\w+\s+){0,2}(?:sent|send\w*|go\w*|see\w*|receiv\w*|leav\w*|shar\w*)\b|\bnever\s+(?:are|is)\b")) continue;
                foreach (var field in sent)
                {
                    var noun = field switch {
                        "rows" => @"\b(?:probe )?rows\b", "value" => @"\b(?:measure )?values?\b",
                        "formula" => @"\bformulas?\b", "member" => @"\bmember names?\b",
                        _ => @"\b" + words[field] + @"s?\b"
                    };
                    if (Regex.IsMatch(clause, noun, RegexOptions.IgnoreCase))
                        errors.Add(label + " denies a sent field (" + field + "): " + clause.Trim());
                }
            }
        }
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }
}
