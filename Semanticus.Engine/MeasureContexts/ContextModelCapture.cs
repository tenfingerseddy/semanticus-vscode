using System;
using System.Linq;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;

namespace Semanticus.Engine.MeasureContexts;

/// <summary>Call on the model thread. The planner only reads the detached result.</summary>
public static class ContextModelCapture
{
    public static ContextModel Capture(Model model)
    {
        var dateTables = model.Tables.Where(t => string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name).Concat(CalendarOps.Enumerate(model).Select(c => c.Table.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parameters = model.Tables.Where(t => t.Columns.Any(c => c.GetExtendedProperty("ParameterMetadata") != null))
            .Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = model.Tables.SelectMany(t => t.Columns.Where(c => c.Type != ColumnType.RowNumber).Select(c =>
            new ContextColumn(t.Name, c.Name, c.DataType is DataType.Int64 or DataType.Double or DataType.Decimal,
                t.IsHidden || c.IsHidden, c.IsKey, dateTables.Contains(t.Name, StringComparer.OrdinalIgnoreCase)
                    || c.DataType == DataType.DateTime)
            {
                FieldParameter = parameters.Contains(t.Name),
                DateTyped = c.DataType == DataType.DateTime,
                GroupByColumns = c.GroupByColumns?.Select(g => RelationshipProbes.ColumnRef(g.Table.Name, g.Name)).ToArray()
                    ?? Array.Empty<string>()
            })).ToArray();
        ContextColumn Column(Column c) => columns.Single(x => x.Table == c.Table.Name && x.Name == c.Name);
        var relationships = model.Relationships.OfType<SingleColumnRelationship>()
            .Where(r => r.FromColumn != null && r.ToColumn != null).Select(r =>
                new ContextRelationship(Column(r.FromColumn), Column(r.ToColumn), r.IsActive,
                    r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections,
                    r.FromCardinality == RelationshipEndCardinality.Many && r.ToCardinality == RelationshipEndCardinality.Many)).ToArray();
        return new(columns, relationships, model.AllMeasures.Select(m =>
            new ContextMeasure(m.Table.Name, m.Name, m.Expression ?? "", m.Description ?? "")).ToArray(), dateTables);
    }
}
