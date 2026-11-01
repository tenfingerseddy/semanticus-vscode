using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    public static partial class ReadinessRuleSet
    {
        private const string PlOrder = "graphicus.pl.order";
        private const string PlSign = "graphicus.pl.sign";
        private const string PlRole = "graphicus.pl.role";
        private const string FunnelOrder = "graphicus.funnel.order";
        private const string FunnelStages = "graphicus.funnel.stages";
        private const string TargetOf = "graphicus.target.of";
        private const string TargetKind = "graphicus.target.kind";

        // ModelRules re-read cross-object inputs on scoped scans, including the opt-in marker.
        private static ModelRule GraphicusRule(string id, string title, Severity severity, FixKind fix,
            Func<Model, ModelRule, RuleEvaluation> evaluate) =>
            new ModelRule(id, title, ReadinessCategory.Graphicus, severity, RuleKind.Deterministic, fix,
                (model, rule) => model.GetAnnotation("graphicus.contract.version") == "1"
                    ? evaluate(model, rule) : new RuleEvaluation());

        private static bool GraphicusInteger(string value, out BigInteger number) =>
            BigInteger.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);

        private static bool GraphicusStages(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            try
            {
                using var json = JsonDocument.Parse(value);
                return json.RootElement.ValueKind == JsonValueKind.Array
                    && json.RootElement.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String);
            }
            catch (JsonException) { return false; }
        }

        private static IEnumerable<ReadinessRule> GraphicusRules()
        {
            yield return GraphicusRule("GR-DESC-MEASURE", "Describe each visible measure", Severity.High, FixKind.AiContent,
                (model, rule) =>
                {
                    var measures = model.AllMeasures.Where(m => !m.IsHidden).ToList();
                    var result = new RuleEvaluation { Applicable = measures.Count };
                    foreach (var measure in measures.Where(m => string.IsNullOrWhiteSpace(m.Description)))
                        result.Violations.Add(rule.NewFinding(measure, measure.Name,
                            $"Measure '{measure.Name}' has no Description. Add a Description that explains what it measures."));
                    return result;
                });

            yield return GraphicusRule("GR-DESC-TABLE", "Describe each visible table", Severity.Medium, FixKind.AiContent,
                (model, rule) =>
                {
                    var tables = model.Tables.Where(t => !t.IsHidden).ToList();
                    var result = new RuleEvaluation { Applicable = tables.Count };
                    foreach (var table in tables.Where(t => string.IsNullOrWhiteSpace(t.Description)))
                        result.Violations.Add(rule.NewFinding(table, table.Name,
                            $"Table '{table.Name}' has no Description. Add a Description that explains what one row represents."));
                    return result;
                });

            yield return GraphicusRule("GR-SYNONYMS", "Add the words your team uses", Severity.Info, FixKind.AiContent,
                (model, rule) =>
                {
                    var culture = PrepForAiReader.SelectLinguisticCulture(model, out _);
                    var objects = new List<TabularNamedObject>();
                    objects.AddRange(model.Tables.Where(t => !t.IsHidden));
                    objects.AddRange(model.AllMeasures.Where(m => !m.IsHidden));
                    objects.AddRange(model.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber));
                    var result = new RuleEvaluation { Applicable = objects.Count };
                    foreach (var obj in objects.Where(o => HasNoSynonyms(o, culture)))
                        result.Violations.Add(rule.NewFinding(obj, obj.Name,
                            $"'{obj.Name}' has no synonyms. Add the words your team uses for it."));
                    return result;
                });

            yield return GraphicusRule("GR-AI-INSTRUCTIONS", "Add AI instructions within the limit", Severity.Info, FixKind.AiContent,
                (model, rule) =>
                {
                    var config = PrepForAiReader.Read(model);
                    // The existing DAC advisory owns the newer format, whose content we cannot yet read.
                    if (config.CopilotToolingFormatPresent) return new RuleEvaluation();
                    var result = new RuleEvaluation { Applicable = 1 };
                    if (string.IsNullOrWhiteSpace(config.AiInstructions))
                        result.Violations.Add(rule.NewFinding(model, model.Name,
                            $"Model '{model.Name}' has no AI instructions. Add business terms and analysis guidance in Prep data for AI."));
                    else if (config.AiInstructionsLength > 10000)
                        result.Violations.Add(rule.NewFinding(model, model.Name,
                            $"Model '{model.Name}' has AI instructions over 10,000 characters. Shorten them to 10,000 characters or fewer."));
                    return result;
                });

            yield return GraphicusRule("GR-AI-DATA-SCHEMA", "Exclude helper fields from the AI data schema", Severity.Info, FixKind.Proposal,
                (model, rule) =>
                {
                    if (PrepForAiReader.Read(model).CopilotToolingFormatPresent) return new RuleEvaluation();
                    var objects = model.Tables.Cast<TabularNamedObject>().Concat(model.AllMeasures).Concat(model.AllColumns)
                        .Where(o => AiSchemaEligible(o) && AiSchemaRuleExcludes(model, o)).ToList();
                    var excluded = AiSchemaExcluded(model);
                    var result = new RuleEvaluation { Applicable = objects.Count };
                    foreach (var obj in objects.Where(o => !IsAiSchemaExcluded(excluded, o)))
                        result.Violations.Add(rule.NewFinding(obj, obj.Name,
                            $"Helper '{obj.Name}' is in the AI data schema. Exclude it in Prep data for AI, or hide it."));
                    return result;
                });

            yield return GraphicusRule("GR-DATE-TABLE", "Mark a date table", Severity.High, FixKind.Proposal,
                (model, rule) =>
                {
                    var result = new RuleEvaluation { Applicable = 1 };
                    if (!model.Tables.Any(t => string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)))
                        result.Violations.Add(rule.NewFinding(model, model.Name,
                            $"Model '{model.Name}' has no marked date table. Mark the table that holds its dates as a date table."));
                    return result;
                });

            yield return GraphicusRule("GR-PL-ORDER", "Give each profit and loss line a unique number", Severity.Medium, FixKind.Proposal,
                (model, rule) =>
                {
                    var measures = model.AllMeasures.Where(m => m.HasAnnotation(PlOrder) || m.HasAnnotation(PlSign) || m.HasAnnotation(PlRole)).ToList();
                    var orders = measures.Where(m => GraphicusInteger(m.GetAnnotation(PlOrder), out _))
                        .GroupBy(m => BigInteger.Parse(m.GetAnnotation(PlOrder), CultureInfo.InvariantCulture))
                        .ToDictionary(g => g.Key, g => g.Count());
                    var result = new RuleEvaluation { Applicable = measures.Count };
                    foreach (var measure in measures)
                        if (!GraphicusInteger(measure.GetAnnotation(PlOrder), out var order) || orders[order] > 1)
                            result.Violations.Add(rule.NewFinding(measure, measure.Name,
                                $"Measure '{measure.Name}' needs a unique whole line number. Set graphicus.pl.order to a whole number used by no other measure."));
                    return result;
                });

            yield return GraphicusRule("GR-PL-SIGN", "Set how each profit and loss line affects profit", Severity.Medium, FixKind.Proposal,
                (model, rule) =>
                {
                    var measures = model.AllMeasures.Where(m => m.HasAnnotation(PlOrder) || m.HasAnnotation(PlSign)).ToList();
                    var result = new RuleEvaluation { Applicable = measures.Count };
                    foreach (var measure in measures.Where(m => m.GetAnnotation(PlSign) != "1" && m.GetAnnotation(PlSign) != "-1"))
                        result.Violations.Add(rule.NewFinding(measure, measure.Name,
                            $"Measure '{measure.Name}' needs a profit sign. Set graphicus.pl.sign to 1 if it adds to profit, or -1 if it subtracts."));
                    return result;
                });

            yield return GraphicusRule("GR-PL-ROLE", "Name each profit and loss line's role", Severity.Medium, FixKind.Proposal,
                (model, rule) =>
                {
                    var roles = new[] { "revenue", "cost_of_sales", "gross_profit", "operating_expense", "operating_profit", "other", "net_profit" };
                    var measures = model.AllMeasures.Where(m => m.HasAnnotation(PlOrder) || m.HasAnnotation(PlRole)).ToList();
                    var result = new RuleEvaluation { Applicable = measures.Count };
                    foreach (var measure in measures.Where(m => !roles.Contains(m.GetAnnotation(PlRole), StringComparer.Ordinal)))
                        result.Violations.Add(rule.NewFinding(measure, measure.Name,
                            $"Measure '{measure.Name}' needs a profit and loss role. Set graphicus.pl.role to one of: {string.Join(", ", roles)}."));
                    return result;
                });

            yield return GraphicusRule("GR-FUNNEL-ORDER", "Set the order of funnel stages", Severity.Medium, FixKind.Proposal,
                (model, rule) =>
                {
                    var objects = model.AllMeasures.Cast<TabularNamedObject>().Concat(model.AllColumns)
                        .Where(o => o is IAnnotationObject a && (a.HasAnnotation(FunnelOrder) || a.HasAnnotation(FunnelStages))).ToList();
                    var result = new RuleEvaluation { Applicable = objects.Count };
                    foreach (var obj in objects)
                    {
                        var annotations = (IAnnotationObject)obj;
                        var order = annotations.HasAnnotation(FunnelOrder);
                        var stages = annotations.HasAnnotation(FunnelStages);
                        if ((order && !GraphicusInteger(annotations.GetAnnotation(FunnelOrder), out _))
                            || (stages && (!(obj is Column) || !GraphicusStages(annotations.GetAnnotation(FunnelStages))))
                            || (obj is Column && order && stages))
                            result.Violations.Add(rule.NewFinding(obj, obj.Name,
                                $"'{obj.Name}' needs a funnel order. Set graphicus.funnel.order to a whole number, or graphicus.funnel.stages on a column to a JSON list of strings. Keep only one on a column."));
                    }
                    return result;
                });

            yield return GraphicusRule("GR-TARGET-OF", "Link each target to its actual measure", Severity.Medium, FixKind.Proposal,
                (model, rule) =>
                {
                    var measures = model.AllMeasures.ToList();
                    var names = new HashSet<string>(measures.Select(m => m.Name), StringComparer.Ordinal);
                    var targets = measures.Where(m => m.HasAnnotation(TargetOf) || m.HasAnnotation(TargetKind)).ToList();
                    var result = new RuleEvaluation { Applicable = targets.Count };
                    foreach (var target in targets)
                    {
                        var kind = target.GetAnnotation(TargetKind);
                        if (!names.Contains(target.GetAnnotation(TargetOf) ?? "") || (kind != "budget" && kind != "target" && kind != "forecast"))
                            result.Violations.Add(rule.NewFinding(target, target.Name,
                                $"Measure '{target.Name}' needs its actual measure and kind. Set graphicus.target.of to an existing measure's exact name, and graphicus.target.kind to budget, target or forecast."));
                    }
                    return result;
                });
        }
    }
}
