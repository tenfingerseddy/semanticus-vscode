using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests;

public sealed class GraphicusReadinessTests
{
    private const string Marker = "graphicus.contract.version";
    private const string Prefix = "graphicus.";
    public static IEnumerable<object[]> Facts => new[]
    {
        "GR-DESC-MEASURE", "GR-DESC-TABLE", "GR-SYNONYMS", "GR-AI-INSTRUCTIONS",
        "GR-AI-DATA-SCHEMA", "GR-DATE-TABLE", "GR-PL-ORDER", "GR-PL-SIGN",
        "GR-PL-ROLE", "GR-FUNNEL-ORDER", "GR-TARGET-OF"
    }.Select(id => new object[] { id });

    // The wrapper binds every new object to the process-wide TabularModelHandler.Singleton, and a model coming to
    // life on another test's dispatcher thread can move it (Windows CI, PR #406). The test holds WrapperGate for
    // the model's whole life, as the product's dispatchers do.
    private sealed class PinnedModel : IDisposable
    {
        private readonly TabularModelHandler _handler;
        private readonly IDisposable _pin;
        public PinnedModel(TabularModelHandler handler) { _handler = handler; _pin = TabularEditor.TOMWrapper.Utils.WrapperGate.Pin(handler); }
        public Model Model => _handler.Model;
        public void Dispose() { _pin.Dispose(); _handler.Dispose(); }
    }

    private static PinnedModel Model()
    {
        var handler = new PinnedModel(TabularEditor.TOMWrapper.Utils.WrapperGate.Construct(() => new TabularModelHandler(1604)));
        var sales = handler.Model.AddTable("Sales");
        sales.AddDataColumn("Amount", "Amount", dataType: DataType.Decimal);
        sales.AddMeasure("Net Sales", "SUM(Sales[Amount])");
        return handler;
    }

    private static RuleEvaluation Eval(Model model, string id)
    {
        PrepForAiReader.Invalidate(model);
        return ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(model);
    }

    private static void Schema(Model model, string instructions, bool excludeHelper, bool synonyms = true, bool excludeAmount = false)
    {
        var culture = model.Cultures.FirstOrDefault() ?? Culture.CreateNew(model, "en-US");
        object Entity(string table, string? property, bool hidden) => new
        {
            Binding = property == null
                ? new Dictionary<string, string> { ["ConceptualEntity"] = table }
                : new Dictionary<string, string> { ["ConceptualEntity"] = table, ["ConceptualProperty"] = property },
            Terms = synonyms ? new object[] { new Dictionary<string, object> { ["team " + (property ?? table)] = new { State = "Generated" } } } : Array.Empty<object>(),
            Visibility = new { Value = hidden ? "Hidden" : "Visible" }
        };
        var entities = new Dictionary<string, object>();
        foreach (var table in model.Tables)
        {
            entities[table.Name] = Entity(table.Name, null, false);
            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
                entities[table.Name + "." + column.Name] = Entity(table.Name, column.Name, excludeAmount && column.Name == "Amount");
            foreach (var measure in table.Measures)
                entities[table.Name + "." + measure.Name] = Entity(table.Name, measure.Name, excludeHelper && measure.Name == "Sales_helper");
        }
        culture.Content = JsonSerializer.Serialize(new
        {
            Version = "1.0.0",
            Language = "en-US",
            CustomInstructions = instructions,
            Entities = entities
        });
    }

    private static void Configure(Model model, string id, bool passing)
    {
        var sales = model.Tables["Sales"];
        var measure = sales.Measures["Net Sales"];
        switch (id)
        {
            case "GR-DESC-MEASURE": measure.Description = passing ? "The amount sold after returns." : " "; break;
            case "GR-DESC-TABLE": sales.Description = passing ? "One row for each sale." : " "; break;
            case "GR-SYNONYMS": Schema(model, "Use net amounts.", false, passing); break;
            case "GR-AI-INSTRUCTIONS": Schema(model, passing ? "Use net amounts." : " ", false); break;
            case "GR-AI-DATA-SCHEMA":
                sales.AddMeasure("Sales_helper", "[Net Sales]");
                Schema(model, "Use net amounts.", passing);
                break;
            case "GR-DATE-TABLE":
                var date = model.AddTable("Date");
                date.AddDataColumn("Date", "Date", dataType: DataType.DateTime).IsKey = true;
                if (passing) date.DataCategory = "Time";
                break;
            case "GR-PL-ORDER": measure.SetAnnotation(Prefix + "pl.order", passing ? "10" : "first"); break;
            case "GR-PL-SIGN":
                measure.SetAnnotation(Prefix + "pl.order", "10");
                if (passing) measure.SetAnnotation(Prefix + "pl.sign", "1");
                break;
            case "GR-PL-ROLE":
                measure.SetAnnotation(Prefix + "pl.order", "10");
                if (passing) measure.SetAnnotation(Prefix + "pl.role", "revenue");
                break;
            case "GR-FUNNEL-ORDER": measure.SetAnnotation(Prefix + "funnel.order", passing ? "1" : "first"); break;
            case "GR-TARGET-OF":
                var target = sales.AddMeasure("Budget Amount", "100");
                target.SetAnnotation(Prefix + "target.of", passing ? "Net Sales" : "Missing Sales");
                target.SetAnnotation(Prefix + "target.kind", "budget");
                break;
            default: throw new ArgumentException(id);
        }
    }

    [Fact]
    public void Fact_ids_and_registered_rules_match_in_both_directions()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "graphicus-ready.v1.json")));
        var facts = contract.RootElement.GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("id").GetString()).OrderBy(x => x).ToArray();
        var rules = ReadinessRuleSet.Default().Where(r => r.Id.StartsWith("GR-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(facts, rules.Select(r => r.Id).OrderBy(x => x).ToArray());
        Assert.All(rules, r =>
        {
            Assert.Equal("Graphicus", r.Category.ToString());
            Assert.Equal(RuleKind.Deterministic, r.Kind);
        });
        Assert.Equal(facts, Facts.Select(row => (string)row[0]).OrderBy(x => x).ToArray());
    }

    [Theory]
    [MemberData(nameof(Facts))]
    public void Rules_are_dormant_without_the_exact_marker(string id)
    {
        using var handler = Model();
        Configure(handler.Model, id, false);
        foreach (var version in new[] { null, "", "2", "01", " 1" })
        {
            if (version == null) handler.Model.RemoveAnnotation(Marker);
            else handler.Model.SetAnnotation(Marker, version);
            var evaluation = Eval(handler.Model, id);
            Assert.Equal(0, evaluation.Applicable);
            Assert.Empty(evaluation.Violations);
        }
    }

    [Theory]
    [MemberData(nameof(Facts))]
    public void Each_fact_checks_a_real_population_and_passes_valid_metadata(string id)
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        Configure(handler.Model, id, true);
        var evaluation = Eval(handler.Model, id);
        Assert.True(evaluation.Applicable > 0, id);
        Assert.Empty(evaluation.Violations);
    }

    [Theory]
    [MemberData(nameof(Facts))]
    public void Each_missed_fact_names_the_object_and_a_fix(string id)
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        Configure(handler.Model, id, false);
        var evaluation = Eval(handler.Model, id);
        Assert.True(evaluation.Applicable > 0, id);
        Assert.NotEmpty(evaluation.Violations);
        Assert.All(evaluation.Violations, finding =>
        {
            Assert.Contains(finding.ObjectName, finding.Message);
            Assert.DoesNotContain("—", finding.Message);
            Assert.True(finding.Message.Length > finding.ObjectName.Length + 15);
            if (id is "GR-SYNONYMS" or "GR-AI-INSTRUCTIONS" or "GR-AI-DATA-SCHEMA")
                Assert.Equal("Info", finding.Severity);
        });
    }

    [Theory]
    [InlineData("GR-PL-ORDER")]
    [InlineData("GR-PL-SIGN")]
    [InlineData("GR-PL-ROLE")]
    [InlineData("GR-FUNNEL-ORDER")]
    [InlineData("GR-TARGET-OF")]
    [InlineData("GR-AI-DATA-SCHEMA")]
    public void Conditional_facts_are_dormant_without_objects_to_check(string id)
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        var evaluation = Eval(handler.Model, id);
        Assert.Equal(0, evaluation.Applicable);
        Assert.Empty(evaluation.Violations);
    }

    [Fact]
    public void Missing_instructions_and_synonyms_are_findings_without_a_schema()
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        Assert.Single(Eval(handler.Model, "GR-AI-INSTRUCTIONS").Violations);
        Assert.Equal(3, Eval(handler.Model, "GR-SYNONYMS").Violations.Count);
    }

    [Theory]
    [InlineData(10000, false)]
    [InlineData(10001, true)]
    public void Instructions_use_the_existing_length_limit(int length, bool missed)
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        Schema(handler.Model, new string('a', length), false);
        Assert.Equal(missed, Eval(handler.Model, "GR-AI-INSTRUCTIONS").Violations.Any());
    }

    [Fact]
    public void Descriptions_ignore_hidden_objects_and_use_the_exact_population()
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        handler.Model.Tables["Sales"].AddMeasure("Hidden Amount", "1").IsHidden = true;
        handler.Model.AddTable("Internal Sales").IsHidden = true;
        foreach (var id in new[] { "GR-DESC-MEASURE", "GR-DESC-TABLE" })
        {
            var evaluation = Eval(handler.Model, id);
            Assert.Equal(1, evaluation.Applicable);
            Assert.Single(evaluation.Violations);
        }
    }

    [Theory]
    [InlineData("10", "010")]
    [InlineData("-10", "-10")]
    public void Pl_order_duplicates_are_numeric_and_cross_tables(string first, string second)
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        handler.Model.Tables["Sales"].Measures["Net Sales"].SetAnnotation(Prefix + "pl.order", first);
        handler.Model.AddTable("Costs").AddMeasure("Cost Amount", "1").SetAnnotation(Prefix + "pl.order", second);
        var evaluation = Eval(handler.Model, "GR-PL-ORDER");
        Assert.Equal(2, evaluation.Applicable);
        Assert.Equal(2, evaluation.Violations.Count);
    }

    [Fact]
    public void Pl_sign_or_role_without_order_reports_the_missing_order()
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        handler.Model.Tables["Sales"].Measures["Net Sales"].SetAnnotation(Prefix + "pl.role", "revenue");
        Assert.Single(Eval(handler.Model, "GR-PL-ORDER").Violations);
    }

    [Theory]
    [InlineData("pl.sign", "1", false)]
    [InlineData("pl.sign", "-1", false)]
    [InlineData("pl.sign", "0", true)]
    [InlineData("pl.sign", "1.0", true)]
    [InlineData("pl.role", "revenue", false)]
    [InlineData("pl.role", "cost_of_sales", false)]
    [InlineData("pl.role", "gross_profit", false)]
    [InlineData("pl.role", "operating_expense", false)]
    [InlineData("pl.role", "operating_profit", false)]
    [InlineData("pl.role", "other", false)]
    [InlineData("pl.role", "net_profit", false)]
    [InlineData("pl.role", "profit", true)]
    [InlineData("pl.role", "Revenue", true)]
    public void Pl_values_use_the_contract_enums(string key, string value, bool missed)
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        var measure = handler.Model.Tables["Sales"].Measures["Net Sales"];
        measure.SetAnnotation(Prefix + "pl.order", "10");
        measure.SetAnnotation(Prefix + key, value);
        Assert.Equal(missed, Eval(handler.Model, key == "pl.sign" ? "GR-PL-SIGN" : "GR-PL-ROLE").Violations.Any());
    }

    [Theory]
    [InlineData("stages", "[\"Lead\",\"Won\"]", false)]
    [InlineData("stages", "[]", false)]
    [InlineData("stages", "[1]", true)]
    [InlineData("stages", "[null]", true)]
    [InlineData("stages", "{}", true)]
    [InlineData("stages", "Lead,Won", true)]
    [InlineData("stages", "", true)]
    [InlineData("order", "1", false)]
    [InlineData("order", "1.5", true)]
    [InlineData("order", "1e2", true)]
    public void Funnel_columns_check_integer_orders_and_string_arrays(string key, string value, bool missed)
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        handler.Model.Tables["Sales"].Columns["Amount"].SetAnnotation(Prefix + "funnel." + key, value);
        Assert.Equal(missed, Eval(handler.Model, "GR-FUNNEL-ORDER").Violations.Any());
    }

    [Fact]
    public void A_funnel_column_cannot_carry_both_annotations()
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        var column = handler.Model.Tables["Sales"].Columns["Amount"];
        column.SetAnnotation(Prefix + "funnel.order", "1");
        column.SetAnnotation(Prefix + "funnel.stages", "[]");
        Assert.Single(Eval(handler.Model, "GR-FUNNEL-ORDER").Violations);
    }

    [Theory]
    [InlineData("Net Sales", "budget", false)]
    [InlineData("Net Sales", "target", false)]
    [InlineData("Net Sales", "forecast", false)]
    [InlineData("Missing Sales", "budget", true)]
    [InlineData("net sales", "budget", true)]
    [InlineData("Net Sales", "Budget", true)]
    [InlineData("Net Sales", null, true)]
    [InlineData(null, "budget", true)]
    [InlineData("", "", true)]
    public void Targets_need_an_exact_existing_measure_and_a_kind(string? actual, string? kind, bool missed)
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        var target = handler.Model.AddTable("Budgets").AddMeasure("Budget Amount", "100");
        if (actual != null) target.SetAnnotation(Prefix + "target.of", actual);
        if (kind != null) target.SetAnnotation(Prefix + "target.kind", kind);
        Assert.Equal(missed, Eval(handler.Model, "GR-TARGET-OF").Violations.Any());
    }

    [Fact]
    public void Ai_schema_checks_the_helper_itself_instead_of_an_unrelated_exclusion()
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        Configure(handler.Model, "GR-AI-DATA-SCHEMA", false);
        Schema(handler.Model, "Use net amounts.", false, excludeAmount: true);
        Assert.Single(Eval(handler.Model, "GR-AI-DATA-SCHEMA").Violations);
    }

    [Fact]
    public void Ai_schema_respects_table_exclusions_and_hidden_helpers()
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        Configure(handler.Model, "GR-AI-DATA-SCHEMA", false);
        var culture = handler.Model.Cultures.First();
        var schema = System.Text.Json.Nodes.JsonNode.Parse(culture.Content)!;
        schema["Entities"]!["Sales"]!["Visibility"]!["Value"] = "Hidden";
        culture.Content = schema.ToJsonString();
        var excluded = Eval(handler.Model, "GR-AI-DATA-SCHEMA");
        Assert.Equal(1, excluded.Applicable);
        Assert.Empty(excluded.Violations);
        handler.Model.Tables["Sales"].Measures["Sales_helper"].IsHidden = true;
        Assert.Equal(0, Eval(handler.Model, "GR-AI-DATA-SCHEMA").Applicable);
    }

    [Fact]
    public void Optional_facts_score_the_graphicus_category_with_positive_rule_weight()
    {
        using var handler = Model();
        handler.Model.SetAnnotation(Marker, "1");
        var rules = ReadinessRuleSet.Default().Where(r => r.Id == "GR-AI-INSTRUCTIONS").ToArray();
        Assert.Single(rules);
        var analyzer = new ReadinessAnalyzer(rules);
        var missing = analyzer.Analyze(handler.Model);
        Assert.Equal(0, missing.Categories.Single(c => c.Category == "Graphicus").Score);
        Schema(handler.Model, "Use net amounts.", false);
        var present = analyzer.Analyze(handler.Model);
        Assert.Equal(100, present.Categories.Single(c => c.Category == "Graphicus").Score);
        Assert.Equal(missing.Overall, present.Overall);
    }

    [Fact]
    public void Scoped_scans_notice_marker_changes_and_cross_object_edits()
    {
        using var handler = Model();
        var model = handler.Model;
        Configure(model, "GR-PL-ORDER", true);
        var analyzer = new ReadinessAnalyzer();
        var state = new ReadinessScanState();
        analyzer.Baseline(model, state);
        model.SetAnnotation(Marker, "1");
        var marked = analyzer.Reanalyze(model, state, new HashSet<string> { "obj:Model" });
        Assert.Contains(marked.Findings, f => f.RuleId == "GR-DESC-MEASURE");
        var cost = model.AddTable("Costs").AddMeasure("Cost Amount", "1");
        cost.SetAnnotation(Prefix + "pl.order", "10");
        var duplicate = analyzer.Reanalyze(model, state, new HashSet<string> { "measure:Costs/Cost Amount" });
        Assert.Equal(2, duplicate.Findings.Count(f => f.RuleId == "GR-PL-ORDER"));
        cost.SetAnnotation(Prefix + "pl.order", "20");
        var repaired = analyzer.Reanalyze(model, state, new HashSet<string> { "measure:Costs/Cost Amount" });
        Assert.DoesNotContain(repaired.Findings, f => f.RuleId == "GR-PL-ORDER");
        model.RemoveAnnotation(Marker);
        var unmarked = analyzer.Reanalyze(model, state, new HashSet<string> { "obj:Model" });
        Assert.DoesNotContain(unmarked.Findings, f => f.RuleId.StartsWith("GR-", StringComparison.Ordinal));
        Assert.False(unmarked.Categories.Single(c => c.Category == "Graphicus").HasRules);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Marker_does_not_change_the_overall_grade_or_existing_categories(bool passing)
    {
        using var handler = Model();
        foreach (var row in Facts) Configure(handler.Model, (string)row[0], passing);
        if (passing)
        {
            foreach (var table in handler.Model.Tables) table.Description = "One row for each business event or date.";
            foreach (var measure in handler.Model.AllMeasures) measure.Description = "The total amount for this business measure.";
            Schema(handler.Model, "Use net amounts.", true);
        }
        var analyzer = new ReadinessAnalyzer();
        var before = analyzer.Analyze(handler.Model);
        handler.Model.SetAnnotation(Marker, "1");
        var after = analyzer.Analyze(handler.Model);
        Assert.Equal(before.Overall, after.Overall);
        Assert.Equal(before.RawOverall, after.RawOverall);
        Assert.Equal(before.Grade, after.Grade);
        Assert.Equal(before.GatedBy, after.GatedBy);
        Assert.Equal(0, Assert.Single(after.Categories, c => c.Category == "Graphicus").Weight);
        foreach (var category in before.Categories.Where(c => c.Category != "Graphicus"))
        {
            var current = after.Categories.Single(c => c.Category == category.Category);
            Assert.Equal(category.Score, current.Score);
            Assert.Equal(category.Applicable, current.Applicable);
            Assert.Equal(category.Violations, current.Violations);
        }
        Assert.Empty(after.RuleErrors);
        Assert.Empty(after.UnevaluatedRules);
        Assert.DoesNotContain(after.ScoredRuleIds, id => id.StartsWith("GR-", StringComparison.Ordinal));
        Assert.Equal(passing, !after.Findings.Any(f => f.RuleId.StartsWith("GR-", StringComparison.Ordinal)));
    }
}
