using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TabularEditor.TOMWrapper;
using Semanticus.Engine.MeasureContexts;
using Semanticus.Dax;
using Semanticus.Dax.Syntax;

namespace Semanticus.Engine;

// Primitive copies only. The worker never reads TOM objects from the query thread.
// Missing: what the formula names that the editing model does not have (the engine's validate_dax reference check).
internal sealed record MeasureMarkFormula(string Ref, string Table, string Name, string Body, string Tag,
    string[] Dependencies, string[] FactTables, string[] NumericColumns, string FormatString = null, string[] Missing = null);

internal sealed class MeasureMarkSnapshot
{
    internal Dictionary<string, MeasureMarkFormula> Formulas { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string[]> Axes { get; } = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> dataParents = new(StringComparer.OrdinalIgnoreCase);
    internal bool HasCalcGroups { get; private set; }
    internal ContextModel ContextModel { get; private set; }
    internal MeasurePerformanceSettings PerformanceSettings { get; private set; }
    internal HashSet<string> TimedMeasures { get; } = new(StringComparer.Ordinal);
    internal TotalsSettings TotalsSettings { get; private set; }
    internal HashSet<string> ExpectedTotals { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> FiltersOnPurpose { get; } = new(StringComparer.Ordinal);

    internal static MeasureMarkSnapshot Capture(Model model)
    {
        var snapshot = new MeasureMarkSnapshot
        {
            HasCalcGroups = model.Tables.OfType<CalculationGroupTable>().Any(),
            ContextModel = ContextModelCapture.Capture(model),
            TotalsSettings = TotalsSettings.Read(model.GetAnnotation("Semanticus.Totals")),
            PerformanceSettings = ReadPerformanceSettings(model.GetAnnotation("Semanticus.Performance"))
        };
        var missing = Semanticus.Analysis.FormulaReferences.HostChecker(model);
        foreach (var measure in model.AllMeasures)
        {
            if (measure.GetAnnotation("Semanticus.Totals.Expected") == "true") snapshot.ExpectedTotals.Add(ObjectRefs.For(measure));
            if (measure.GetAnnotation(LocalEngine.FiltersOnPurposeAnnotation) == "true") snapshot.FiltersOnPurpose.Add(ObjectRefs.For(measure));
            if (bool.TryParse(measure.GetAnnotation("Semanticus.Performance.Include"), out var timed) && timed)
                snapshot.TimedMeasures.Add(ObjectRefs.For(measure));
            var dependencies = measure.DependsOn.Keys.ToArray();
            var factTables = dependencies.OfType<Column>().Select(c => c.Table.Name)
                .Concat(dependencies.OfType<Table>().Select(t => t.Name)).Distinct().ToArray();
            if (factTables.Length == 0 && !dependencies.OfType<Measure>().Any()) factTables = new[] { measure.Table.Name };
            snapshot.Formulas[ObjectRefs.For(measure)] = new MeasureMarkFormula(ObjectRefs.For(measure), measure.Table.Name,
                measure.Name, measure.Expression, measure.LineageTag,
                dependencies.OfType<Measure>().Select(ObjectRefs.For).ToArray(),
                factTables,
                dependencies.OfType<Column>().Where(c => c.DataType.ToString() is "Int64" or "Double" or "Decimal")
                    .Select(c => RelationshipProbes.ColumnRef(c.Table.Name, c.Name)).ToArray(), measure.FormatString,
                missing(measure.Expression, measure.Table.Name));
        }
        var keyColumns = model.Relationships.OfType<SingleColumnRelationship>()
            .SelectMany(r => new[] { r.FromColumn, r.ToColumn }).ToHashSet();
        foreach (var rel in model.Relationships.OfType<SingleColumnRelationship>())
        {
            var fact = rel.FromColumn?.Table; var dimension = rel.ToColumn?.Table;
            if (fact == null || dimension == null) continue;
            if (rel.FromCardinality.ToString() != "Many" && rel.ToCardinality.ToString() == "Many")
                (fact, dimension) = (dimension, fact);
            if (rel.FromCardinality == RelationshipEndCardinality.Many && rel.ToCardinality == RelationshipEndCardinality.One
                || rel.FromCardinality == RelationshipEndCardinality.One && rel.ToCardinality == RelationshipEndCardinality.Many)
                snapshot.dataParents[fact.Name] = snapshot.dataParents.GetValueOrDefault(fact.Name, Array.Empty<string>())
                    .Append(dimension.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!rel.IsActive) continue;
            var column = dimension.Columns.Where(c => c.Type != ColumnType.RowNumber && !c.IsHidden && !keyColumns.Contains(c))
                .OrderBy(c => c.DataType.ToString() == "String" ? 0 : 1).ThenBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault();
            if (column == null) continue;
            var axis = RelationshipProbes.ColumnRef(dimension.Name, column.Name);
            snapshot.Axes[fact.Name] = snapshot.Axes.GetValueOrDefault(fact.Name, Array.Empty<string>()).Append(axis).Distinct().ToArray();
        }
        return snapshot;
    }

    private static MeasurePerformanceSettings ReadPerformanceSettings(string json)
    {
        try
        {
            var settings = string.IsNullOrWhiteSpace(json) ? new MeasurePerformanceSettings()
                : System.Text.Json.JsonSerializer.Deserialize<MeasurePerformanceSettings>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            if (!double.IsFinite(settings.SlowThresholdMs) || settings.SlowThresholdMs < 0) settings.SlowThresholdMs = 3000;
            if (!double.IsFinite(settings.RegressionPercent) || settings.RegressionPercent < 0) settings.RegressionPercent = 30;
            if (!double.IsFinite(settings.RegressionMs) || settings.RegressionMs < 0) settings.RegressionMs = 200;
            return settings;
        }
        catch (System.Text.Json.JsonException) { return new(); }
    }

    internal string PreviousReference(MeasureMarkFormula current)
    {
        if (Formulas.ContainsKey(current.Ref)) return current.Ref;
        if (string.IsNullOrEmpty(current.Tag)) return null;
        var matches = Formulas.Values.Where(f => f.Tag == current.Tag).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Ref : null;
    }

    internal MeasureMarkFormula[] Closure(string reference)
    {
        var result = new List<MeasureMarkFormula>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string r)
        {
            if (!seen.Add(r) || !Formulas.TryGetValue(r, out var f)) return;
            result.Add(f); foreach (var dep in f.Dependencies) Visit(dep);
        }
        Visit(reference); return result.ToArray();
    }

    /// <summary>Why no mark can run this measure on the connected model, or null. A formula that names a measure or column the
    /// editing model does not have (a measure it used was deleted here) would read the connected model's own definition of
    /// that name, so its checks would pass for a formula that no longer works (fx29b C). Each such check is Unknown, with this.</summary>
    internal string MissingReason(string reference)
    {
        var names = Closure(reference).SelectMany(f => f.Missing ?? Array.Empty<string>()).Select(MissingName)
            .Distinct(StringComparer.Ordinal).ToArray();
        return names.Length == 0 ? null : "The formula uses " + string.Join(", ", names) + (names.Length == 1 ? ", which is" : ", which are")
            + " not in this model. Fix the formula, then check it again.";
    }

    // The reference check's messages (DaxValidator.References), read back to the name the person typed: [Total Sales],
    // 'Sales'[Amount] or 'Sales'. The messages quote the raw name, so an apostrophe inside it is not doubled: each shape is read
    // whole, anchored at its own words (fx30e K2: [Owner's Total] read "[Owner").
    private static string MissingName(string message)
    {
        static string Table(string name) => "'" + name.Replace("'", "''") + "'";
        var m = System.Text.RegularExpressions.Regex.Match(message, @"^Table '(.*)' has no column '(.*)'\.$");
        if (m.Success) return Table(m.Groups[1].Value) + "[" + m.Groups[2].Value + "]";
        m = System.Text.RegularExpressions.Regex.Match(message, @"^Unknown table '(.*)'\.$");
        if (m.Success) return Table(m.Groups[1].Value);
        m = System.Text.RegularExpressions.Regex.Match(message, @"^Unknown measure or column '(\[.*\])'\.$");
        return m.Success ? m.Groups[1].Value : message;
    }

    // The bodies a comparison ran on: the measure and every measure it uses. An edit of any of them gives a new value.
    internal string ClosureInputs(string reference) => MeasureRunPlanner.Digest(System.Text.Json.JsonSerializer.Serialize(
        Closure(reference).OrderBy(f => f.Ref, StringComparer.Ordinal).Select(f => new { f.Ref, f.Table, f.Name, f.Body })));

    internal string[] RelatedAxes(string reference) => Closure(reference).SelectMany(f => f.FactTables)
        .Distinct().SelectMany(t => Axes.GetValueOrDefault(t, Array.Empty<string>())).Distinct().ToArray();

    internal string FactExpression(string reference)
    {
        var closure = Closure(reference);
        var column = closure.SelectMany(f => f.NumericColumns).FirstOrDefault();
        if (column != null) return "SUM(" + column + ")";
        var table = closure.SelectMany(f => f.FactTables).FirstOrDefault();
        return table == null ? null : "COUNTROWS(" + RelationshipProbes.QuoteTable(table) + ")";
    }

    internal string FactRowsExpression(string reference)
    {
        var formula = Formulas[reference];
        var measure = ContextModel.Measures.Single(m => m.Table == formula.Table && m.Name == formula.Name);
        // Filter arguments name dimensions too. Count the data tables, including transitive measure reads.
        var tables = new ContextCandidateFinder().Find(new(ContextModel, measure)).DataTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A date argument or a dimension condition does not make that dimension another fact source.
        // Keep a dimension when it is the only source, such as an average of Product[Unit Price].
        var dimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { table };
            var pending = new Queue<string>(dataParents.GetValueOrDefault(table, Array.Empty<string>()));
            while (pending.TryDequeue(out var parent))
            {
                if (!seen.Add(parent)) continue;
                dimensions.Add(parent);
                foreach (var ancestor in dataParents.GetValueOrDefault(parent, Array.Empty<string>())) pending.Enqueue(ancestor);
            }
        }
        tables.ExceptWith(dimensions);
        return tables.Count == 0 ? null : string.Join(" + ", tables.OrderBy(t => t, StringComparer.Ordinal).Select(t =>
            "COALESCE(COUNTROWS(" + RelationshipProbes.QuoteTable(t) + "), 0)"));
    }

    private sealed record FilterRowVariable(Func<string> Rows, Func<bool> Nonzero, Func<bool> ReplacesFilter);

    // Count the rows the formula uses, retaining its filter arguments. Equal aggregate values cannot
    // establish this: a distinct count, average, minimum or maximum can repeat across different rows.
    internal string FilterRowsExpression(string reference, bool preserveFilterChanges = true)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var nextVariable = 0;
        string Function(CallExpressionSyntax call) => call.Name is IdentifierNameSyntax name ? name.Identifier.ValueText.ToUpperInvariant() : "";
        IEnumerable<SyntaxNode> Nodes(SyntaxNode node) => new[] { node }.Concat(node.ChildNodes().SelectMany(Nodes));
        bool ChangesContext(SyntaxNode node) => Nodes(node).OfType<CallExpressionSyntax>()
            .Any(c => Function(c) is "CALCULATE" or "CALCULATETABLE" or "TOTALYTD" or "TOTALMTD" or "TOTALQTD");
        ExpressionSyntax Unwrap(ExpressionSyntax node)
        {
            while (node is ParenthesizedExpressionSyntax parens) node = parens.Expression;
            return node;
        }
        bool Blank(ExpressionSyntax node) => Unwrap(node) is null
            || Unwrap(node) is CallExpressionSyntax call && Function(call) == "BLANK" && call.ArgumentList.Arguments.Count == 0;
        bool Nonzero(ExpressionSyntax node, Dictionary<string, FilterRowVariable> variables)
        {
            node = Unwrap(node);
            if (node is IdentifierNameSyntax name)
                return variables != null && variables.TryGetValue(name.Identifier.ValueText, out var variable) && variable.Nonzero();
            return double.TryParse(node?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value != 0;
        }
        bool ReplacesFilter(ExpressionSyntax node, Dictionary<string, FilterRowVariable> variables)
        {
            node = Unwrap(node);
            if (node is IdentifierNameSyntax name)
                return variables != null && variables.TryGetValue(name.Identifier.ValueText, out var variable) && variable.ReplacesFilter();
            return node is CallExpressionSyntax call
                && Function(call) is "ALL" or "ALLSELECTED" or "ALLEXCEPT" or "REMOVEFILTERS" or "CROSSFILTER" or "USERELATIONSHIP";
        }
        string ValueRows(IEnumerable<ExpressionSyntax> branches, string current, Dictionary<string, FilterRowVariable> variables,
            bool singleValueBranch = false)
        {
            var rows = branches.Where(b => !Blank(b)).Select(b => Count(b, current, variables)).ToArray();
            // A blank guard has one value path. Equal witness text cannot identify which other branch supplied the rows.
            return rows.Length == 0 || singleValueBranch && rows.Length != 1 || rows.Any(r => r == null)
                || rows.Distinct(StringComparer.Ordinal).Count() != 1 ? null : rows[0];
        }
        string Formula(string current)
        {
            if (!visiting.Add(current) || !Formulas.TryGetValue(current, out var formula)) return null;
            try {
                var tree = DaxSyntaxTree.ParseExpression(formula.Body ?? "");
                if (tree.GetDiagnostics().Any(d => d.Severity == DaxDiagnosticSeverity.Error)) return null;
                return Count(tree.GetExpressionRoot().Expression, current);
            }
            finally { visiting.Remove(current); }
        }
        string Count(ExpressionSyntax node, string current, Dictionary<string, FilterRowVariable> variables = null)
        {
            node = Unwrap(node);
            if (node == null || node is LiteralExpressionSyntax || Blank(node)) return null;
            if (node is VariableExpressionSyntax block)
            {
                var locals = variables == null ? new Dictionary<string, FilterRowVariable>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, FilterRowVariable>(variables, StringComparer.OrdinalIgnoreCase);
                var declarations = new List<Func<string>>();
                foreach (var declaration in block.Declarations)
                {
                    var prior = new Dictionary<string, FilterRowVariable>(locals, StringComparer.OrdinalIgnoreCase);
                    string rowName;
                    do { rowName = "__smx_filter_rows_" + nextVariable++; }
                    while ((Formulas[current].Body ?? "").Contains(rowName, StringComparison.OrdinalIgnoreCase));
                    string rows = null;
                    var counted = false;
                    string VariableRows()
                    {
                        if (!counted) { counted = true; rows = Count(declaration.Initializer, current, prior); }
                        return rows == null ? null : rowName;
                    }
                    locals[declaration.Identifier.ValueText] = new(VariableRows,
                        () => Nonzero(declaration.Initializer, prior), () => ReplacesFilter(declaration.Initializer, prior));
                    // A scalar variable is evaluated where declared, before any later CALCULATE changes context.
                    declarations.Add(() => declaration + (rows == null ? "" : " VAR " + rowName + " = " + rows));
                }
                var result = Count(block.Return.Expression, current, locals);
                return result == null ? null : string.Join(" ", declarations.Select(d => d())) + " RETURN " + result;
            }
            if (node is IdentifierNameSyntax identifier)
                return variables != null && variables.TryGetValue(identifier.Identifier.ValueText, out var variable) ? variable.Rows() : null;
            if (node is BracketedNameSyntax name)
            {
                var dependency = Formulas[current].Dependencies.FirstOrDefault(r =>
                    Formulas.TryGetValue(r, out var f) && f.Name.Equals(name.Identifier.ValueText, StringComparison.OrdinalIgnoreCase));
                return dependency == null ? null : Formula(dependency);
            }
            if (node is CallExpressionSyntax call)
            {
                var args = call.ArgumentList.Arguments.Select(a => (a as ArgumentSyntax)?.Expression).ToArray();
                var function = Function(call);
                if (function == "IF" && args.Length is 2 or 3)
                    return ValueRows(args.Skip(1), current, variables, singleValueBranch: true);
                if (function == "SWITCH" && args.Length >= 3)
                    return ValueRows(args.Where((_, i) => i >= 2 && i % 2 == 0)
                        .Concat(args.Length % 2 == 0 ? args.TakeLast(1) : Array.Empty<ExpressionSyntax>()), current, variables, singleValueBranch: true);
                if (function == "DIVIDE" && args.Length is 2 or 3
                    && !Blank(args[0]) && !Blank(args[1])
                    && (args.Length == 2 || Blank(args[2]) || Unwrap(args[2]) is LiteralExpressionSyntax))
                    return ValueRows(args.Take(2).Where(a => !Nonzero(a, variables)), current, variables);
                if (function == "CALCULATE" && args.Length > 0)
                {
                    var rows = Count(args[0], current, variables);
                    var filters = args.Skip(1).Where(a => a != null && (preserveFilterChanges || !ReplacesFilter(a, variables))).ToArray();
                    return rows == null || filters.Length == 0 ? rows
                        : "CALCULATE(" + rows + string.Concat(filters.Select(a => ", " + a)) + ")";
                }
                if (function is "SUMX" or "AVERAGEX" or "MINX" or "MAXX" or "COUNTX" or "COUNTAX" or "COUNTROWS")
                {
                    if (args.Length == 0 || args[0] == null) return null;
                    var table = args[0];
                    if (table is IdentifierNameSyntax tableName && variables?.ContainsKey(tableName.Identifier.ValueText) == true)
                        return null; // A table variable needs its own filtered table witness.
                    if (!preserveFilterChanges && table is CallExpressionSyntax all && Function(all) == "ALL")
                        table = all.ArgumentList.Arguments.OfType<ArgumentSyntax>().FirstOrDefault()?.Expression ?? table;
                    return "COALESCE(COUNTROWS(" + table + "), 0)";
                }
                if (function is "SUM" or "AVERAGE" or "MIN" or "MAX" or "COUNT" or "COUNTA" or "DISTINCTCOUNT" or "DISTINCTCOUNTNOBLANK")
                    return FactRowsExpression(current);
            }
            // Do not claim an ignored filter from arithmetic over different filtered branches.
            return Closure(current).Any(f => ChangesContext(DaxSyntaxTree.ParseExpression(f.Body ?? "").GetExpressionRoot().Expression))
                ? null : FactRowsExpression(current);
        }
        return Formula(reference);
    }

    internal string FilterRowsUnavailableReason(string reference, bool usesTimeLogic)
    {
        const string next = " Add a number you trust to check it.";
        var expressions = Closure(reference).Select(f => DaxSyntaxTree.ParseExpression(f.Body ?? "").GetExpressionRoot().Expression).ToArray();
        ExpressionSyntax Result(ExpressionSyntax node) => node switch {
            ParenthesizedExpressionSyntax parens => Result(parens.Expression),
            VariableExpressionSyntax block => Result(block.Return.Expression),
            _ => node
        };
        if (Result(expressions[0]) is BinaryExpressionSyntax binary && binary.OperatorToken.Kind is
            SyntaxKind.EqualsToken or SyntaxKind.StrictEqualsToken or SyntaxKind.NotEqualsToken or SyntaxKind.LessThanToken
            or SyntaxKind.LessThanOrEqualsToken or SyntaxKind.GreaterThanToken or SyntaxKind.GreaterThanOrEqualsToken
            or SyntaxKind.AndAndToken or SyntaxKind.OrOrToken)
            return (usesTimeLogic ? "This measure checks which dates to show, so a row count cannot check its result."
                : "This measure returns a yes-or-no result, so a row count cannot check it.") + next;
        IEnumerable<SyntaxNode> Nodes(SyntaxNode node) => new[] { node }.Concat(node.ChildNodes().SelectMany(Nodes));
        var functions = expressions.SelectMany(Nodes).OfType<CallExpressionSyntax>()
            .Select(c => (c.Name as IdentifierNameSyntax)?.Identifier.ValueText.ToUpperInvariant()).ToArray();
        if (functions.Any(f => f is "TOTALYTD" or "TOTALMTD" or "TOTALQTD" or "DATESYTD" or "DATESMTD" or "DATESQTD"
            or "DATEADD" or "DATESINPERIOD" or "DATESBETWEEN" or "SAMEPERIODLASTYEAR" or "PARALLELPERIOD"))
            return "This formula moves the date window, so the row count test does not apply." + next;
        if (functions.Any(f => f is "IF" or "SWITCH" or "DIVIDE"))
            return "This formula can use different sets of rows, so one row count cannot check its filters." + next;
        return "A row count is not available for this formula. Add a number you trust to check its filters.";
    }

    internal bool SameClosure(MeasureMarkSnapshot other, string reference)
    {
        var before = Closure(reference); var after = other.Closure(reference);
        return before.Length == after.Length && before.All(f => other.Formulas.TryGetValue(f.Ref, out var n)
            && f.Body == n.Body && f.Table == n.Table && f.Name == n.Name);
    }

    internal string Apply(string query, string reference)
    {
        var definitions = new StringBuilder();
        foreach (var f in Closure(reference))
            definitions.Append("    MEASURE ").Append(RelationshipProbes.QuoteTable(f.Table)).Append(Baseline.MeasureRefExpr(f.Name))
                .Append(" = (\n").Append(f.Body).Append("\n)\n");
        var trimmed = query.TrimStart();
        return trimmed.StartsWith("DEFINE", StringComparison.OrdinalIgnoreCase)
            ? "DEFINE\n" + definitions + trimmed[6..]
            : "DEFINE\n" + definitions + query;
    }

    // One small grid: the real grand total and five axis members. Both versions use the same member set.
    internal static string GridQuery(string name, string axis)
    {
        var total = DaxBench.BuildProbeQuery(Baseline.MeasureRefExpr(name), Array.Empty<string>(), Array.Empty<string>());
        if (string.IsNullOrEmpty(axis)) return total;
        var filter = "KEEPFILTERS(TOPN(5, ALL(" + axis + "), " + axis + ", ASC))";
        var grouped = DaxBench.BuildProbeQuery(Baseline.MeasureRefExpr(name), new[] { axis }, new[] { filter });
        var order = grouped.LastIndexOf("\nORDER BY", StringComparison.Ordinal);
        if (order >= 0) grouped = grouped[..order];
        return "EVALUATE\nUNION(\nSELECTCOLUMNS(" + total["EVALUATE".Length..]
            + ", \"__mark_context\", \"Grand total\", \"v\", [v]),\nSELECTCOLUMNS(" + grouped["EVALUATE".Length..]
            + ", \"__mark_context\", \"" + MeasureMarkJudge.ColumnName(axis).Replace("\"", "\"\"") + ": \" & " + axis + " & \"\", \"v\", [v]))";
    }
}
