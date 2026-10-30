using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Dax;
using Semanticus.Dax.Syntax;

namespace Semanticus.Engine.MeasureContexts;

public record ContextCandidates(IReadOnlyList<ContextCandidate> Candidates, IReadOnlyList<string> Diagnostics, bool UsesTimeLogic)
{
    public IReadOnlyList<string> DataTables { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ContextFilterClear> FilterClears { get; init; } = Array.Empty<ContextFilterClear>();
}

/// <summary>Reads the existing DAX parser's nodes. It never scans formula text for names or function calls.</summary>
public sealed class ContextCandidateFinder
{
    public ContextCandidates Find(ContextPlanningRequest request) => new Finder(request).Find();

    private sealed record Reference(string Table, ContextColumn Column = null, ContextMeasure Measure = null);
    private sealed record Touch(ContextColumn Column, string Family, string Reason, string Expected, ContextRole Role);
    private sealed record RelationshipChange(ContextColumn From, ContextColumn To, string Function, string Direction);

    private static readonly HashSet<string> Iterators = new(StringComparer.OrdinalIgnoreCase) {
        "SUMX", "AVERAGEX", "MINX", "MAXX", "COUNTX", "COUNTAX", "PRODUCTX", "CONCATENATEX", "RANKX",
        "STDEVX.P", "STDEVX.S", "VARX.P", "VARX.S", "MEDIANX", "PERCENTILEX.INC", "PERCENTILEX.EXC", "GEOMEANX", "LINESTX",
        "FILTER", "ADDCOLUMNS", "SELECTCOLUMNS", "GENERATE", "GENERATEALL", "TOPN", "SAMPLE"
    };
    private static readonly HashSet<string> TimeFunctions = new(StringComparer.OrdinalIgnoreCase) {
        "TOTALYTD", "TOTALQTD", "TOTALMTD", "TOTALWTD", "DATESYTD", "DATESQTD", "DATESMTD", "DATESWTD",
        "DATEADD", "DATESINPERIOD", "DATESBETWEEN", "SAMEPERIODLASTYEAR", "PARALLELPERIOD",
        "PREVIOUSYEAR", "PREVIOUSQUARTER", "PREVIOUSMONTH", "PREVIOUSWEEK", "PREVIOUSDAY",
        "NEXTYEAR", "NEXTQUARTER", "NEXTMONTH", "NEXTWEEK", "NEXTDAY",
        "STARTOFYEAR", "STARTOFQUARTER", "STARTOFMONTH", "STARTOFWEEK", "ENDOFYEAR", "ENDOFQUARTER", "ENDOFMONTH", "ENDOFWEEK",
        "OPENINGBALANCEYEAR", "OPENINGBALANCEQUARTER", "OPENINGBALANCEMONTH", "OPENINGBALANCEWEEK",
        "CLOSINGBALANCEYEAR", "CLOSINGBALANCEQUARTER", "CLOSINGBALANCEMONTH", "CLOSINGBALANCEWEEK",
        "FIRSTDATE", "LASTDATE", "FIRSTNONBLANK", "LASTNONBLANK", "FIRSTNONBLANKVALUE", "LASTNONBLANKVALUE",
        "YEAR", "QUARTER", "MONTH", "DAY", "WEEKDAY", "WEEKNUM", "DATEDIFF", "EDATE", "EOMONTH", "TODAY", "NOW", "NETWORKDAYS"
    };
    private static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase) {
        "ALL", "ALLEXCEPT", "ALLSELECTED", "REMOVEFILTERS", "KEEPFILTERS", "USERELATIONSHIP", "CROSSFILTER", "TREATAS", "FILTER", "VALUES"
    };

    private sealed class Finder
    {
        private readonly ContextPlanningRequest _request;
        private readonly ContextModel _model;
        private readonly Dictionary<string, ContextColumn> _columns;
        private readonly Dictionary<string, ContextMeasure> _measures;
        private readonly HashSet<string> _tables;
        private readonly HashSet<string> _readTables = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dataTables = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ContextColumn> _readColumns = new();
        private readonly List<Touch> _touches = new();
        private readonly List<Touch> _rowTouches = new();
        private readonly Dictionary<ContextMeasure, Reference[]> _measureData = new();
        private readonly HashSet<ContextMeasure> _resultMeasures = new();
        private readonly HashSet<(ContextMeasure Measure, string Family, string Reason, string Expected)> _globalClears = new();
        private readonly List<RelationshipChange> _changes = new();
        private readonly List<string> _diagnostics = new();
        private readonly Dictionary<string, ContextCandidate> _candidates = new(StringComparer.OrdinalIgnoreCase);
        private bool _time;

        internal Finder(ContextPlanningRequest request)
        {
            _request = request;
            _model = request.Model;
            _columns = _model.Columns.ToDictionary(c => Key(c.Table, c.Name), StringComparer.OrdinalIgnoreCase);
            _measures = _model.Measures.Append(request.Measure).GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            _tables = _model.Columns.Select(c => c.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        internal ContextCandidates Find()
        {
            var resultCalls = ResultCalls();
            var pending = new Queue<ContextMeasure>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Enqueue(_request.Measure);
            while (pending.Count > 0)
            {
                var measure = pending.Dequeue();
                if (!visited.Add(Key(measure.Table, measure.Name))) continue;
                var tree = DaxSyntaxTree.ParseExpression(measure.Formula ?? "");
                foreach (var diagnostic in tree.GetDiagnostics().Where(d => d.Severity == DaxDiagnosticSeverity.Error))
                    _diagnostics.Add($"{Words(measure.Name)}: {diagnostic.Code} at {diagnostic.Span.Start}.");
                var root = tree.GetExpressionRoot().Expression;
                foreach (var reference in References(root, measure))
                {
                    if (reference.Measure != null) pending.Enqueue(reference.Measure);
                    else
                    {
                        _readTables.Add(reference.Table);
                        if (reference.Column != null) _readColumns.Add(reference.Column);
                        if (reference.Column != null && IsTime(reference.Column)
                            || _model.DateTables.Contains(reference.Table, StringComparer.OrdinalIgnoreCase)) _time = true;
                    }
                }
                _measureData[measure] = ResultNodes(root, dataOnly: true).OfType<NameSyntax>()
                    .Select(n => Resolve(n, measure)).Where(r => r != null).ToArray();
                foreach (var reference in _measureData[measure].Where(r => r.Measure == null && _resultMeasures.Contains(measure)))
                    _dataTables.Add(reference.Table);
                foreach (var call in Nodes(root).OfType<CallExpressionSyntax>())
                    if (resultCalls.Contains((measure, call.Span.Start))) ReadCall(call, measure);
            }
            // A global clear cannot remove a filter from a disconnected control. Resolve its scope after reading helpers.
            foreach (var clear in _globalClears)
            {
                var data = DataTablesFor(clear.Measure);
                foreach (var column in _model.Columns.Where(c => CanFilterRead(c.Table, data)))
                    AddTouch(column, clear.Family, clear.Reason, clear.Expected);
            }
            foreach (var relationship in _model.Relationships.Where(r => Direction(r) == "NONE" && !CanFilterRead(r.To.Table)))
                foreach (var column in TableColumns(relationship.To.Table))
                    AddTouch(column, "CROSSFILTER", "The formula blocks this dimension's filter path.",
                        "This dimension should leave the result unchanged because the formula blocks its filter path.");
            var clears = _touches.Where(t => MeasureContextPlan.IsFilterClear(t.Family, t.Expected))
                .Select(t => new ContextFilterClear(t.Column, t.Family)).Distinct().ToList();
            foreach (var clear in _globalClears.Where(g => !clears.Any(c => c.Function == g.Family)))
                clears.Add(new(null, clear.Family));
            BuildCandidates();
            return new(_candidates.Values.ToArray(), _diagnostics.Distinct().ToArray(), _time)
                { DataTables = _dataTables.OrderBy(t => t, StringComparer.Ordinal).ToArray(), FilterClears = clears.ToArray() };
        }

        private void ReadCall(CallExpressionSyntax call, ContextMeasure measure)
        {
            var name = Name(call.Name).ToUpperInvariant();
            var args = call.ArgumentList.Arguments.OfType<ArgumentSyntax>().Select(a => a.Expression).ToArray();
            if (name is "CALCULATE" or "CALCULATETABLE")
            {
                foreach (var arg in args.Skip(1))
                {
                    var unwrapped = Unwrap(arg);
                    if (unwrapped is CallExpressionSyntax modifier && Modifiers.Contains(Name(modifier.Name))) continue;
                    TouchReferences(arg, measure, name, "The formula sets a filter on this column.",
                        "The formula should overwrite the slicer on this column.");
                }
            }
            else if (name is "ALL" or "REMOVEFILTERS" or "ALLSELECTED")
            {
                var expected = name == "ALLSELECTED" ? "Keep filters from outside the visual and remove the current row filter."
                    : "The formula should clear incoming filters on this column.";
                var reason = name == "ALLSELECTED" ? "The formula separates the visual's row filters from outside selections."
                    : "The formula removes filters from this column.";
                if (args.Length == 0)
                    _globalClears.Add((measure, name, reason, expected));
                foreach (var arg in args) TouchReferences(arg, measure, name, reason, expected, expandTable: true);
            }
            else if (name == "ALLEXCEPT" && args.Length > 0)
            {
                var kept = args.Skip(1).SelectMany(a => References(a, measure))
                    .SelectMany(r => r.Column != null ? new[] { r.Column } : ExpandedColumns(r.Table)).ToHashSet();
                foreach (var table in References(args[0], measure).Select(r => r.Table).Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (var column in ExpandedColumns(table).Where(c => !c.Hidden))
                        AddTouch(column, name, kept.Contains(column) ? "The formula keeps this column while clearing its siblings."
                            : "The formula clears this column while keeping named siblings.",
                            kept.Contains(column) ? "The formula should keep this column's slicer." : "The formula should clear this column's slicer.");
            }
            else if (name == "KEEPFILTERS")
            {
                foreach (var arg in args) TouchReferences(arg, measure, name,
                    "The formula keeps the person's selection and applies another filter.",
                    "The formula should intersect its filter with the person's slicer.");
            }
            else if (name == "VALUES")
            {
                foreach (var arg in args) TouchReferences(arg, measure, name,
                    "The formula reads the visible members, including a possible blank member.",
                    "Use the visible members left by the person's selection.");
            }
            else if (name == "FILTER")
            {
                foreach (var arg in args) TouchReferences(arg, measure, name,
                    "The formula tests rows of this table before it calculates the result.",
                    "Keep the rows that pass the formula's test.");
            }
            else if (name is "USERELATIONSHIP" or "CROSSFILTER")
            {
                var targets = args.Take(2).SelectMany(a => References(a, measure)).Where(r => r.Column != null).Select(r => r.Column).ToArray();
                foreach (var column in targets) AddTouch(column, name,
                    name == "USERELATIONSHIP" ? "The formula selects the relationship used by this column."
                        : "The formula changes the direction in which filters cross this relationship.",
                    name == "USERELATIONSHIP" ? "Use the relationship named by the formula, even when it is inactive."
                        : "Use the filter direction named by the formula.");
                if (targets.Length == 2)
                    _changes.Add(new(targets[0], targets[1], name, args.Length > 2 ? Name(args[2]).ToUpperInvariant() : ""));
            }
            else if (name == "TREATAS")
            {
                foreach (var arg in args.Skip(1)) TouchReferences(arg, measure, name,
                    "The formula transfers selected values to this column.",
                    "The formula should transfer values onto this column's filter.");
            }
            var nonDateBlankFunction = name is "FIRSTNONBLANK" or "LASTNONBLANK" or "FIRSTNONBLANKVALUE" or "LASTNONBLANKVALUE"
                && !args.Take(1).SelectMany(a => References(a, measure)).Any(r => r.Column != null && IsTime(r.Column)
                    || _model.DateTables.Contains(r.Table, StringComparer.OrdinalIgnoreCase));
            if (TimeFunctions.Contains(name) && !nonDateBlankFunction)
            {
                _time = true;
                var dateReferences = args.SelectMany(a => References(a, measure)).ToArray();
                const string reason = "The formula uses time logic, so compare periods on this column.";
                const string expected = "Use the requested period and compare its boundaries.";
                foreach (var reference in dateReferences.Where(r => r.Column != null && IsTime(r.Column)))
                    AddTouch(reference.Column, "TIME", reason, expected, ContextRole.Time);
                var dates = dateReferences.Where(r => _model.DateTables.Contains(r.Table, StringComparer.OrdinalIgnoreCase))
                    .Select(r => r.Table).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (dates.Length == 0) dates = _model.DateTables;
                if (dates.Length == 0) dates = dateReferences.Where(r => r.Column?.Time == true)
                    .Select(r => r.Table).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var table in dates)
                    foreach (var column in TableColumns(table).Where(c => !c.Hidden))
                        AddTouch(column, "TIME", reason, expected, ContextRole.Time);
            }
            var iteratorTableArgument = IteratorTableArgument(name);
            if (Iterators.Contains(name) && args.Length > iteratorTableArgument)
            {
                // Variables evaluated outside the iterator keep their value. Only calls inside its body cause transition.
                var transition = args.Skip(iteratorTableArgument + 1).Any(a => Nodes(a).Any(n =>
                    n is CallExpressionSyntax c && (Eq(Name(c.Name), "CALCULATE") || Eq(Name(c.Name), "CALCULATETABLE"))
                    || n is NameSyntax s && s.Parent is not QualifiedNameSyntax && Resolve(s, measure)?.Measure != null));
                if (transition)
                    TouchReferences(args[iteratorTableArgument], measure, "ITERATOR", "A called measure converts each iterator row into filters.",
                        "Check the context transition from each iterator row.", ContextRole.Iterator);
                else
                    foreach (var reference in References(args[iteratorTableArgument], measure).Where(r => r.Measure == null))
                        foreach (var column in reference.Column != null ? new[] { reference.Column } : TableColumns(reference.Table).Where(c => !c.Hidden))
                            _rowTouches.Add(new(column, "ITERATOR", "The iterator evaluates this table one row at a time.",
                                "Check the row context used by the iterator.", ContextRole.Iterator));
            }
        }

        private HashSet<(ContextMeasure, int)> ResultCalls()
        {
            var calls = new HashSet<(ContextMeasure, int)>();
            var pending = new Queue<ContextMeasure>();
            pending.Enqueue(_request.Measure);
            while (pending.Count > 0)
            {
                var measure = pending.Dequeue();
                if (!_resultMeasures.Add(measure)) continue;
                var root = DaxSyntaxTree.ParseExpression(measure.Formula ?? "").GetExpressionRoot().Expression;
                foreach (var node in ResultNodes(root))
                {
                    if (node is CallExpressionSyntax call) calls.Add((measure, call.Span.Start));
                    if (node is NameSyntax name && Resolve(name, measure)?.Measure is { } helper) pending.Enqueue(helper);
                }
            }
            return calls;
        }

        private static IEnumerable<SyntaxNode> ResultNodes(SyntaxNode root, bool dataOnly = false)
        {
            var pending = new Stack<SyntaxNode>(); pending.Push(root);
            var visited = new HashSet<(int, SyntaxKind)>();
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (!visited.Add((node.Span.Start, node.Kind))) continue;
                yield return node;
                if (node is VariableExpressionSyntax block)
                { pending.Push(block.Return.Expression); continue; }
                if (node is IdentifierNameSyntax identifier && VariableValue(identifier) is { } value)
                { pending.Push(value); continue; }
                if (node is NameSyntax) continue;
                // A comparison produces a guard, not the numeric result whose filters we describe.
                if (node is BinaryExpressionSyntax binary && binary.OperatorToken.Kind is
                    SyntaxKind.EqualsToken or SyntaxKind.StrictEqualsToken or SyntaxKind.NotEqualsToken or SyntaxKind.LessThanToken
                    or SyntaxKind.LessThanOrEqualsToken or SyntaxKind.GreaterThanToken or SyntaxKind.GreaterThanOrEqualsToken
                    or SyntaxKind.AndAndToken or SyntaxKind.OrOrToken) continue;
                if (node is CallExpressionSyntax call)
                {
                    var args = call.ArgumentList.Arguments.OfType<ArgumentSyntax>().Select(a => a.Expression).ToArray();
                    var name = Name(call.Name).ToUpperInvariant();
                    if (dataOnly && name is "USERELATIONSHIP" or "CROSSFILTER") continue;
                    // Guards and unused variables cannot make a result ignore a slicer.
                    var results = dataOnly && name is "CALCULATE" or "CALCULATETABLE" or "TREATAS" ? args.Take(1)
                        : name is "IF" or "IF.EAGER" ? args.Skip(1)
                        : name == "SWITCH" ? args.Where((_, i) => i > 0 && (i % 2 == 0 || i == args.Length - 1))
                        : name == "FILTER" ? args.Take(1) : args.AsEnumerable();
                    foreach (var result in results.Reverse()) pending.Push(result);
                    continue;
                }
                foreach (var child in node.ChildNodes().Reverse()) pending.Push(child);
            }
        }

        private void TouchReferences(ExpressionSyntax expression, ContextMeasure measure, string family, string reason,
            string expected, ContextRole role = ContextRole.Filter, bool expandTable = false)
        {
            foreach (var reference in References(expression, measure).Where(r => r.Measure == null))
            {
                if (reference.Column != null) AddTouch(reference.Column, family, reason, expected, role);
                else foreach (var column in (expandTable ? ExpandedColumns(reference.Table) : TableColumns(reference.Table)).Where(c => !c.Hidden))
                    AddTouch(column, family, reason, expected, role);
            }
        }

        private void AddTouch(ContextColumn column, string family, string reason, string expected, ContextRole role = ContextRole.Filter)
            => _touches.Add(new(column, family, reason, expected, role));

        private void BuildCandidates()
        {
            bool Eligible(ContextColumn c) => !c.Hidden && !c.FieldParameter && (_time || !IsTime(c));
            if (!_touches.Any(t => Eligible(t.Column))) _touches.AddRange(_rowTouches);
            var touches = _touches.Where(t => Eligible(t.Column)).GroupBy(t => Key(t.Column.Table, t.Column.Name), StringComparer.OrdinalIgnoreCase).ToArray();
            var touchedTables = _touches.Where(t => _time || !IsTime(t.Column)).Select(t => t.Column.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var group in touches)
            {
                var first = group.First();
                var families = group.Select(t => t.Family).Distinct().ToArray();
                var expected = string.Join(" ", group.Select(t => t.Expected).Distinct());
                var reason = string.Join(" ", group.Select(t => t.Reason).Distinct());
                Add(first.Column, ContextShape.Single, first.Role, reason, expected, 0, families);
                Add(first.Column, ContextShape.Several, first.Role, "Compare several selected members. " + reason, expected, 1, families);
            }
            foreach (var table in touchedTables)
                foreach (var column in TableColumns(table).Where(c => Eligible(c) && !c.Key && !c.Numeric
                    && !touches.Any(g => g.First().Column == c)))
                {
                    var relationshipFamilies = _touches.Where(t => Eq(t.Column.Table, table)
                        && t.Family is "USERELATIONSHIP" or "CROSSFILTER").Select(t => t.Family).Distinct().ToArray();
                    Add(column, ContextShape.Group, ContextRole.Sibling,
                        relationshipFamilies.Length > 0 ? "Check how the formula's relationship change affects this visible dimension column."
                            : "Check which sibling filters survive the formula's filter changes.",
                        relationshipFamilies.Length > 0 ? CanFilterRead(table)
                                ? "This dimension should split the result through the formula's relationship."
                                : "This dimension should leave the result unchanged because the formula blocks its filter path."
                            : "Keep this sibling filter unless the formula clears its table.",
                        10, relationshipFamilies);
                }

            var paths = PathsFromReads();
            foreach (var pair in paths.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (touchedTables.Contains(pair.Key) || _readTables.Contains(pair.Key)) continue;
                var column = BestColumn(TableColumns(pair.Key).Where(Eligible));
                if (column == null) continue;
                var path = pair.Value;
                var inactive = path.Any(r => !r.Active && !Changed(r, "USERELATIONSHIP"));
                var blocked = !CanFilterRead(pair.Key);
                var reason = "Check the relationship path and its blank member for unmatched keys.";
                if (path.Any(r => r.ManyToMany)) reason += " The path has a many-to-many relationship.";
                reason += path.Any(r => r.TwoWay || Direction(r) == "BOTH") ? " Filters can travel in both directions." : " Check the single filter direction.";
                var expected = inactive && blocked ? "The inactive relationship should leave the result unchanged unless the formula activates it."
                    : blocked ? "The relationship direction should leave the result unchanged."
                    : "This dimension should split the result through the relationship path.";
                Add(column, ContextShape.Group, ContextRole.Related, reason, expected, 20, Array.Empty<string>(), path.Select(PathText).ToArray());
            }
            // A disconnected dimension is a negative control: it repeats a value because no filter path reaches the data.
            foreach (var table in _tables.OrderBy(t => t, StringComparer.Ordinal).Where(t => !_readTables.Contains(t)
                && !touchedTables.Contains(t) && !paths.ContainsKey(t)))
            {
                var column = BestColumn(TableColumns(table).Where(Eligible));
                if (column == null) continue;
                if (_candidates.Values.Count(c => c.Role == ContextRole.Control) >= 2) break;
                Add(column, ContextShape.Group, ContextRole.Control, "This dimension has no relationship path to the formula's data.",
                    "The result should repeat because this dimension cannot filter the data.", 40, Array.Empty<string>());
            }
            if (touches.Length == 0)
                foreach (var column in _readColumns.Distinct().Where(Eligible))
                    Add(column, ContextShape.Group, ContextRole.Related, "The formula reads this column directly.",
                        "Compare the result across the visible members of this column.", 30, Array.Empty<string>());

            var anchor = _candidates.Values.Where(c => c.Column != null && c.Shape == ContextShape.Group)
                .OrderBy(c => c.Priority).ThenBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault()?.Column
                ?? _candidates.Values.FirstOrDefault()?.Column
                ?? BestColumn(_model.Columns.Where(Eligible));
            _candidates["total"] = new("total", null, ContextShape.GrandTotal, ContextRole.Total,
                "Check the measure with no row or column grouping.", "Recalculate the formula at the grand total.", 90,
                Array.Empty<string>(), Array.Empty<string>());
            if (anchor != null)
            {
                var detail = _touches.Select(t => t.Column).FirstOrDefault(c => Eligible(c) && c.Table == anchor.Table && c != anchor)
                    ?? BestColumn(TableColumns(anchor.Table).Where(c => Eligible(c) && c != anchor));
                Add(anchor, ContextShape.Subtotal, ContextRole.Total, "Check one subtotal level before the grand total.",
                    "Recalculate the formula at this subtotal; compare it with its detail rows.", 91, Array.Empty<string>());
                var id = Id(anchor, ContextShape.Subtotal);
                _candidates[id] = _candidates[id] with { DetailColumn = detail };
            }
            // Fill only to the minimum. Do not add irrelevant fact amounts to an already useful plan.
            foreach (var column in _model.Columns.Where(Eligible)
                .OrderBy(c => c.Key).ThenBy(c => c.Numeric).ThenBy(c => c.Table, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                if (_candidates.Count >= RuleContextOrderer.MinimumContexts) break;
                if (!_readTables.Contains(column.Table) && !paths.ContainsKey(column.Table)
                    && !_candidates.Values.Any(c => c.Column?.Table == column.Table)) continue;
                foreach (var shape in new[] { ContextShape.Group, ContextShape.Single, ContextShape.Several })
                {
                    if (_candidates.Count >= RuleContextOrderer.MinimumContexts) break;
                    Add(column, shape, ContextRole.Related, CanFilterRead(column.Table)
                            ? "Compare another selection on a table relevant to this measure."
                            : "This dimension has no relationship path to the formula's data.",
                        CanFilterRead(column.Table) ? "This selection should split the result unless the formula changes its filters."
                            : "This selection should leave the result unchanged because no active filter path reaches the data.",
                        50, Array.Empty<string>());
                }
            }
        }

        private void Add(ContextColumn column, ContextShape shape, ContextRole role, string reason, string expected,
            int priority, string[] families, string[] path = null)
        {
            var id = Id(column, shape);
            if (_candidates.ContainsKey(id)) return;
            _candidates[id] = new(id, column, shape, role, Words(reason), Words(expected), priority, families, path ?? Array.Empty<string>());
        }

        private Dictionary<string, ContextRelationship[]> PathsFromReads()
        {
            var result = new Dictionary<string, ContextRelationship[]>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            foreach (var table in _dataTables.OrderBy(t => t, StringComparer.Ordinal)) { result[table] = Array.Empty<ContextRelationship>(); queue.Enqueue(table); }
            while (queue.Count > 0)
            {
                var table = queue.Dequeue();
                foreach (var relationship in _model.Relationships.Where(r => Eq(r.From.Table, table) || Eq(r.To.Table, table)))
                {
                    var next = Eq(relationship.From.Table, table) ? relationship.To.Table : relationship.From.Table;
                    if (result.ContainsKey(next)) continue;
                    result[next] = result[table].Append(relationship).ToArray();
                    queue.Enqueue(next);
                }
            }
            return result;
        }

        private HashSet<string> DataTablesFor(ContextMeasure source)
        {
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<ContextMeasure>();
            var pending = new Queue<ContextMeasure>(); pending.Enqueue(source);
            while (pending.Count > 0)
            {
                var measure = pending.Dequeue();
                if (!visited.Add(measure) || !_measureData.TryGetValue(measure, out var references)) continue;
                foreach (var reference in references)
                    if (reference.Measure != null) pending.Enqueue(reference.Measure);
                    else tables.Add(reference.Table);
            }
            return tables;
        }

        private bool CanFilterRead(string table, HashSet<string> data = null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { table };
            var queue = new Queue<string>(); queue.Enqueue(table);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if ((data ?? _dataTables).Contains(current)) return true;
                foreach (var r in _model.Relationships.Where(r => r.Active || Changed(r, "USERELATIONSHIP")))
                {
                    var change = _changes.LastOrDefault(c => c.Function == "CROSSFILTER" && Matches(c, r));
                    var direction = Direction(r);
                    if (direction == "NONE") continue;
                    var next = direction == "ONEWAY_LEFTFILTERSRIGHT" ? Eq(change.From.Table, current) ? change.To.Table : null
                        : direction == "ONEWAY_RIGHTFILTERSLEFT" ? Eq(change.To.Table, current) ? change.From.Table : null
                        : Eq(r.To.Table, current) ? r.From.Table
                        : (r.TwoWay || direction == "BOTH") && Eq(r.From.Table, current) ? r.To.Table : null;
                    if (next != null && seen.Add(next)) queue.Enqueue(next);
                }
            }
            return false;
        }

        private bool Changed(ContextRelationship relationship, string function) => _changes.Any(c => c.Function == function && Matches(c, relationship));
        private string Direction(ContextRelationship relationship)
        {
            var direction = _changes.LastOrDefault(c => c.Function == "CROSSFILTER" && Matches(c, relationship))?.Direction ?? "";
            return direction switch { "0" => "NONE", "1" => "ONEWAY", "2" => "BOTH", _ => direction };
        }
        private static bool Matches(RelationshipChange change, ContextRelationship relationship)
            => (SameColumn(change.From, relationship.From) && SameColumn(change.To, relationship.To))
                || (SameColumn(change.To, relationship.From) && SameColumn(change.From, relationship.To));
        private static bool SameColumn(ContextColumn a, ContextColumn b) => Eq(a.Table, b.Table) && Eq(a.Name, b.Name);
        private static string PathText(ContextRelationship r) => r.From.Dax + " -> " + r.To.Dax
            + (r.Active ? " (active" : " (inactive") + (r.TwoWay ? ", both directions" : ", one direction") + (r.ManyToMany ? ", many-to-many)" : ")");

        private IEnumerable<Reference> References(SyntaxNode root, ContextMeasure measure, bool dataOnly = false)
        {
            var stack = new Stack<SyntaxNode>(); stack.Push(root);
            var visited = new HashSet<(int, SyntaxKind)>();
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (!visited.Add((node.Span.Start, node.Kind))) continue;
                if (node is CallExpressionSyntax call)
                {
                    var arguments = call.ArgumentList.Arguments.OfType<ArgumentSyntax>();
                    if (dataOnly)
                    {
                        var function = Name(call.Name).ToUpperInvariant();
                        if (function is "USERELATIONSHIP" or "CROSSFILTER") continue;
                        if (function is "CALCULATE" or "CALCULATETABLE" or "TREATAS") arguments = arguments.Take(1);
                    }
                    foreach (var argument in arguments.Reverse()) stack.Push(argument.Expression);
                    continue;
                }
                if (node is NameSyntax name)
                {
                    if (name is IdentifierNameSyntax identifier && VariableValue(identifier) is { } value)
                    { stack.Push(value); continue; }
                    var reference = Resolve(name, measure);
                    if (reference != null) yield return reference;
                    continue;
                }
                foreach (var child in node.ChildNodes().Reverse()) stack.Push(child);
            }
        }

        private Reference Resolve(NameSyntax name, ContextMeasure measure)
        {
            if (name is QualifiedNameSyntax qualified)
            {
                var table = Name(qualified.Qualifier);
                var member = qualified.Name.Identifier.ValueText;
                if (_columns.TryGetValue(Key(table, member), out var column)) return new(table, column);
                if (_measures.TryGetValue(member, out var target) && Eq(target.Table, table)) return new(table, Measure: target);
                return null;
            }
            if (name is BracketedNameSyntax bracketed)
            {
                var member = bracketed.Identifier.ValueText;
                if (_measures.TryGetValue(member, out var target)) return new(target.Table, Measure: target);
                if (RowColumn(bracketed, measure) is { } rowColumn) return new(rowColumn.Table, rowColumn);
                if (_columns.TryGetValue(Key(measure.Table, member), out var column)) return new(column.Table, column);
                var matches = _model.Columns.Where(c => Eq(c.Name, member)).Take(2).ToArray();
                return matches.Length == 1 ? new(matches[0].Table, matches[0]) : null;
            }
            var tableName = Name(name);
            return _tables.Contains(tableName) && !(name is IdentifierNameSyntax i && VariableValue(i) != null) ? new(tableName) : null;
        }

        private static ExpressionSyntax VariableValue(IdentifierNameSyntax name)
        {
            for (var parent = name.Parent; parent != null; parent = parent.Parent)
                if (parent is VariableExpressionSyntax variables)
                {
                    var declaration = variables.Declarations.LastOrDefault(d => d.Span.Start < name.Span.Start
                        && Eq(d.Identifier.ValueText, name.Identifier.ValueText));
                    if (declaration != null) return declaration.Initializer;
                }
            return null;
        }

        private ContextColumn RowColumn(BracketedNameSyntax name, ContextMeasure measure)
        {
            for (var parent = name.Parent; parent != null; parent = parent.Parent)
            {
                if (parent is not CallExpressionSyntax call || !Iterators.Contains(Name(call.Name))) continue;
                var first = call.ArgumentList.Arguments.OfType<ArgumentSyntax>().ElementAtOrDefault(IteratorTableArgument(Name(call.Name)))?.Expression;
                if (first == null || name.Span.Start < first.Span.End) continue;
                foreach (var reference in References(first, measure).Where(r => r.Measure == null))
                    if (_columns.TryGetValue(Key(reference.Table, name.Identifier.ValueText), out var column)) return column;
            }
            return null;
        }

        private IEnumerable<ContextColumn> ExpandedColumns(string table)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { table };
            var queue = new Queue<string>(); queue.Enqueue(table);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var column in TableColumns(current)) yield return column;
                foreach (var r in _model.Relationships.Where(r => r.Active && !r.ManyToMany && Eq(r.From.Table, current)))
                    if (seen.Add(r.To.Table)) queue.Enqueue(r.To.Table);
            }
        }

        private bool IsTime(ContextColumn column) => column.Time || _model.DateTables.Contains(column.Table, StringComparer.OrdinalIgnoreCase)
            || _model.Relationships.Any(r => SameColumn(r.From, column) && _model.DateTables.Contains(r.To.Table, StringComparer.OrdinalIgnoreCase)
                || SameColumn(r.To, column) && _model.DateTables.Contains(r.From.Table, StringComparer.OrdinalIgnoreCase));
        private IEnumerable<ContextColumn> TableColumns(string table) => _model.Columns.Where(c => Eq(c.Table, table));
        private static ContextColumn BestColumn(IEnumerable<ContextColumn> columns) => columns.Where(c => !c.Hidden)
            .OrderBy(c => c.Numeric).ThenBy(c => c.Key).ThenBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
    }

    private static string Key(string table, string member) => table.Length + ":" + table + member;
    private static int IteratorTableArgument(string name) => Eq(name, "TOPN") || Eq(name, "SAMPLE") ? 1 : 0;
    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Words(string value) => string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
    private static string Id(ContextColumn column, ContextShape shape)
    {
        var prefix = shape switch { ContextShape.Group => "group:", ContextShape.Subtotal => "subtotal:", _ => "slice:" };
        return prefix + Uri.EscapeDataString(column.Table) + "/" + Uri.EscapeDataString(column.Name)
            + (shape == ContextShape.Single ? ":single" : shape == ContextShape.Several ? ":several" : "");
    }
    private static ExpressionSyntax Unwrap(ExpressionSyntax node)
    {
        while (node is ParenthesizedExpressionSyntax parentheses) node = parentheses.Expression;
        return node;
    }
    private static string Name(SyntaxNode node) => node switch {
        IdentifierNameSyntax n => n.Identifier.ValueText, QuotedNameSyntax n => n.Identifier.ValueText,
        BracketedNameSyntax n => n.Identifier.ValueText, LiteralExpressionSyntax n => n.Token.ValueText,
        DottedNameSyntax n => string.Join(".", n.Segments.Select(s => s.Identifier.ValueText)), _ => ""
    };
    private static IEnumerable<SyntaxNode> Nodes(SyntaxNode root)
    {
        var stack = new Stack<SyntaxNode>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop(); yield return node;
            foreach (var child in node.ChildNodes().Reverse()) stack.Push(child);
        }
    }
}
