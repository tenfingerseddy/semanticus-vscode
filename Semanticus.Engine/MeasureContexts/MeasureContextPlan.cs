using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine.MeasureContexts;

public record ContextColumn(string Table, string Name, bool Numeric = false, bool Hidden = false,
    bool Key = false, bool Time = false)
{
    public string Dax => "'" + Table.Replace("'", "''") + "'[" + Name.Replace("]", "]]") + "]";
    public bool FieldParameter { get; init; }
    public bool DateTyped { get; init; }
    public string[] GroupByColumns { get; init; } = Array.Empty<string>();
}
public record ContextRelationship(ContextColumn From, ContextColumn To, bool Active = true,
    bool TwoWay = false, bool ManyToMany = false);
public record ContextMeasure(string Table, string Name, string Formula, string Description = "");
public record ContextModel(ContextColumn[] Columns, ContextRelationship[] Relationships,
    ContextMeasure[] Measures, string[] DateTables)
{
    /// <summary>A label's key is required even when the key is hidden. Keep the requested column first for member reads.</summary>
    public string[] ExpandGroupBy(IEnumerable<string> columns)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string dax)
        {
            if (string.IsNullOrWhiteSpace(dax) || !seen.Add(dax.Trim())) return;
            dax = dax.Trim();
            result.Add(dax);
            var column = Columns.FirstOrDefault(c => string.Equals(c.Dax, dax, StringComparison.OrdinalIgnoreCase));
            foreach (var key in column?.GroupByColumns ?? Array.Empty<string>()) Add(key);
        }
        foreach (var column in columns ?? Array.Empty<string>()) Add(column);
        return result.ToArray();
    }
}
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
[Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
public enum ContextShape { Single, Several, Group, GrandTotal, Subtotal }
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
[Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
public enum ContextRole { Filter, Sibling, Related, Control, Iterator, Time, Total }
public record ContextCandidate(string Id, ContextColumn Column, ContextShape Shape, ContextRole Role,
    string Reason, string ExpectedBehaviour, int Priority, string[] Families, string[] RelationshipPath)
{
    /// <summary>Grouped probes must retain the blank member, including unmatched relationship keys.</summary>
    public bool IncludeBlank { get; init; } = true;
    /// <summary>The detail level below a subtotal, when the model supplies a second column.</summary>
    public ContextColumn DetailColumn { get; init; }
}
public record ContextPlanningRequest(ContextModel Model, ContextMeasure Measure);
public record ContextFilterClear(ContextColumn Column, string Function);
public record ContextOrderRequest(string Name, string Description, string Formula,
    IReadOnlyList<ContextCandidate> Candidates);

/// <summary>The marks door calls this interface once and uses the returned contexts for all three marks.</summary>
public interface IMeasureContextPlanner
{
    MeasureContextPlan Plan(ContextPlanningRequest request);
}

/// <summary>Jev can replace the rule order after its context choices have been measured.</summary>
public interface IContextOrderer
{
    IReadOnlyList<ContextCandidate> Order(ContextOrderRequest request);
}

public record MeasureContextPlan(IReadOnlyList<ContextCandidate> Contexts, IReadOnlyList<string> Diagnostics)
{
    public bool UsesTimeLogic { get; init; }
    public ContextColumn DataDateColumn { get; init; }
    // Keep clears that can feed the returned value, before the probe cap and visibility rules select contexts.
    public IReadOnlyList<ContextFilterClear> FilterClears { get; init; } = Array.Empty<ContextFilterClear>();
    internal ContextCandidate[] ChosenContexts => Contexts
        .DistinctBy(c => c.Column?.Dax ?? c.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static bool IsFilterClear(string family, string expected) => family is "ALL" or "REMOVEFILTERS" or "ALLSELECTED"
        || family == "ALLEXCEPT" && expected.Contains("clear this column's slicer", StringComparison.OrdinalIgnoreCase)
        || family == "CROSSFILTER" && expected.Contains("blocks its filter path", StringComparison.OrdinalIgnoreCase);

    // Filters allow an explicit clear on this column without changing which contexts Totals judges.
    internal bool ClearsFilter(ContextCandidate candidate) => candidate.Column != null
        && (FilterClears.Any(c => c.Column == candidate.Column) || Contexts.Any(c => c.Column == candidate.Column
            && c.Families.Any(f => IsFilterClear(f, c.ExpectedBehaviour))));
    internal bool ExpectsConstant(ContextCandidate candidate)
    {
        // A fixed CALCULATE filter replaces its column's slicer. Controls and blocked paths also repeat by design.
        var fixedColumn = candidate.Column != null && Contexts.Any(c => c.Column == candidate.Column
            && c.Families.Contains("CALCULATE") && c.ExpectedBehaviour.Contains("overwrite", StringComparison.OrdinalIgnoreCase));
        return fixedColumn || candidate.Role == ContextRole.Control
            || candidate.ExpectedBehaviour.Contains("leave the result unchanged", StringComparison.OrdinalIgnoreCase)
            || candidate.ExpectedBehaviour.Contains("repeat", StringComparison.OrdinalIgnoreCase);
    }
    internal bool ExpectsConstant(ContextColumn column) => column != null
        && Contexts.Any(c => c.Column == column && ExpectsConstant(c));

    public IReadOnlyList<ContextCandidate> ForMark(string mark) => mark switch
    {
        "Filters" or "Contexts" or "Totals" => Contexts,
        _ => throw new ArgumentException("This plan supports Filters, Contexts and Totals.", nameof(mark))
    };
}

public sealed class MeasureContextPlanner : IMeasureContextPlanner
{
    private readonly IContextOrderer _orderer;
    public MeasureContextPlanner(IContextOrderer orderer = null) => _orderer = orderer ?? new RuleContextOrderer();
    public MeasureContextPlan Plan(ContextPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var found = new ContextCandidateFinder().Find(request);
        var ordered = _orderer.Order(new(request.Measure.Name, request.Measure.Description,
            request.Measure.Formula, found.Candidates));
        return new(ordered.ToArray(), found.Diagnostics) {
            UsesTimeLogic = found.UsesTimeLogic,
            FilterClears = found.FilterClears,
            DataDateColumn = found.UsesTimeLogic ? DataDate(request.Model, found.DataTables) : null
        };
    }

    private static ContextColumn DataDate(ContextModel model, IReadOnlyList<string> dataTables)
    {
        IEnumerable<ContextColumn> Dates(string table) => model.Columns
            .Where(c => c.Table == table && c.DateTyped && !c.FieldParameter);
        // Select through the data relationship, not the measure's time argument. A wrong date argument must still fail.
        var related = model.Relationships.Where(r => r.Active && dataTables.Contains(r.From.Table, StringComparer.OrdinalIgnoreCase))
            .OrderBy(r => model.DateTables.Contains(r.To.Table, StringComparer.OrdinalIgnoreCase) ? 0 : r.To.DateTyped ? 1 : 2)
            .SelectMany(r => Dates(r.To.Table).OrderBy(c => c == r.To ? 0 : c.Key ? 1 : 2));
        return related.Concat(dataTables.SelectMany(Dates)).FirstOrDefault();
    }
}
