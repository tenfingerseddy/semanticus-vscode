using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis;

/// <summary>Structural hints, not proofs of business meaning. Suggestions never write DAX.</summary>
internal static class WrongNumberReadiness
{
    internal static IEnumerable<ReadinessRule> Rules()
    {
        yield return new ModelRule("AVG-OF-RATIO", "An average of percentages can give a wrong total",
            ReadinessCategory.BestPractice, Severity.High, RuleKind.Deterministic, FixKind.Proposal,
            (m, rule) =>
            {
                var ev = new RuleEvaluation();
                foreach (var measure in m.AllMeasures)
                {
                    if (!AverageOfRatio(measure)) continue;
                    ev.Applicable++;
                    ev.Violations.Add(rule.NewFinding(measure, measure.Name,
                        "This measure averages row percentages. That gives each row equal weight and may differ from dividing the totals. Compare both numbers before choosing."));
                }
                return ev;
            });
        yield return new ModelRule("SEMI-ADDITIVE-SUM", "A balance may be added across dates",
            ReadinessCategory.BestPractice, Severity.Medium, RuleKind.Deterministic, FixKind.None,
            (m, rule) =>
            {
                var ev = new RuleEvaluation();
                foreach (var measure in m.AllMeasures)
                {
                    var state = SnapshotSum(measure, new HashSet<Measure>());
                    if (!state.applies) continue;
                    if (state.unsafeSum)
                        ev.Violations.Add(rule.NewFinding(measure, measure.Name,
                            "This measure sums a balance or stock amount. Adding balances across dates may overstate the result. Check whether it needs the last date with data. Keep the sum if these are movements."));
                }
                // BestPractice uses the violation population so clean constructs cannot lift the grade.
                ev.Applicable = ev.Violations.Count;
                return ev;
            });
        yield return new ModelRule("DAX-DATE-LITERAL", "A date filter uses a fixed date or year",
            ReadinessCategory.BestPractice, Severity.Medium, RuleKind.Deterministic, FixKind.None,
            (m, rule) =>
            {
                var ev = new RuleEvaluation();
                foreach (var measure in m.AllMeasures)
                {
                    var tokens = DaxScan.Tokenize(measure.Expression);
                    var ranges = FilterRanges(tokens).ToArray();
                    if (!ranges.Any(r => HasDateInput(measure, tokens, r.start, r.end))) continue;
                    if (ranges.Any(r => HasFixedDate(measure, tokens, r.start, r.end)))
                        ev.Violations.Add(rule.NewFinding(measure, measure.Name,
                            "This measure filters on a fixed date or year. It can become out of date after a refresh. Anchor the window to the latest date with data, or state the fixed window in the description."));
                }
                ev.Applicable = ev.Violations.Count;
                return ev;
            });
    }

    internal static ReadinessRule KeyRule(ReadinessLiveStats stats) => new ModelRule(
        "KEY-INTEGRITY", "Some keys do not match or appear more than once",
        ReadinessCategory.Relationships, Severity.High, RuleKind.Dmv, FixKind.None,
        (m, rule) =>
        {
            var ev = new RuleEvaluation();
            foreach (var check in KeyChecks(m))
            {
                if (!stats.KeyIntegrity.TryGetValue(check.ObjectRef, out var count)) continue;
                if (!string.IsNullOrEmpty(count.Error))
                {
                    ev.Errors.Add("Key counts could not be read for " + check.Label + ": " + count.Error);
                    continue;
                }
                ev.Applicable++;
                if (count.UnmatchedRows > 0 || count.DuplicateRows > 0)
                    ev.Violations.Add(rule.NewFinding(check.Relationship, check.Label,
                        FormattableString.Invariant($"{count.UnmatchedRows} rows have no matching key. The one side has {count.DuplicateRows} extra rows with repeated keys. Unmatched rows can appear under a blank label. These counts cover the data your sign-in can read. Nothing was changed.")));
            }
            return ev;
        });

    internal sealed class KeyCheck
    {
        public SingleColumnRelationship Relationship;
        public string ObjectRef;
        public string Label;
        public string Query;
    }

    internal static IEnumerable<KeyCheck> KeyChecks(Model model)
    {
        foreach (var r in model.Relationships.OfType<SingleColumnRelationship>())
        {
            if (r.FromColumn == null || r.ToColumn == null) continue;
            Column fact, dimension;
            if (r.ToCardinality == RelationshipEndCardinality.One)
            { fact = r.FromColumn; dimension = r.ToColumn; }
            else if (r.FromCardinality == RelationshipEndCardinality.One)
            { fact = r.ToColumn; dimension = r.FromColumn; }
            else continue;
            var ft = QuoteTable(fact.Table.Name);
            var dt = QuoteTable(dimension.Table.Name);
            var fk = ColumnDax(fact);
            var dk = ColumnDax(dimension);
            yield return new KeyCheck
            {
                Relationship = r, ObjectRef = Refs.For(r), Label = fk + " to " + dk,
                // ALLNOBLANKROW excludes the relationship's synthetic unknown row, not real blank keys.
                // Transfer counts only. Never return key values or change the relationship.
                Query = $"EVALUATE ROW(\"UnmatchedRows\", COALESCE(COUNTROWS(FILTER(ALLNOBLANKROW({ft}), NOT CONTAINS(ALLNOBLANKROW({dk}), {dk}, {fk}))), 0), \"DuplicateRows\", COUNTROWS(ALLNOBLANKROW({dt})) - COUNTROWS(SUMMARIZE(ALLNOBLANKROW({dt}), {dk})))"
            };
        }
    }

    internal static string QuoteTable(string name) => "'" + name.Replace("'", "''") + "'";
    internal static string ColumnDax(Column c) => QuoteTable(c.Table.Name) + "[" + c.Name.Replace("]", "]]") + "]";
    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static TabularNamedObject Reference(Measure owner, IReadOnlyList<DaxScan.Tok> tokens, int i)
    {
        var token = tokens[i];
        if (token.Kind != DaxScan.Kind.Name || token.Delim != '[') return null;
        var prev = i > 0 ? tokens[i - 1] : null;
        var tableName = prev?.Kind == DaxScan.Kind.Name && prev.Delim == '\'' ? prev.Inner :
            prev?.Kind == DaxScan.Kind.Word ? prev.Text : null;
        var table = owner.Model.Tables.FirstOrDefault(t => Eq(t.Name, tableName));
        if (table != null)
            return (TabularNamedObject)table.Columns.FirstOrDefault(c => Eq(c.Name, token.Inner))
                ?? table.Measures.FirstOrDefault(m => Eq(m.Name, token.Inner));
        return (TabularNamedObject)owner.Model.AllMeasures.FirstOrDefault(m => Eq(m.Name, token.Inner))
            ?? owner.Table.Columns.FirstOrDefault(c => Eq(c.Name, token.Inner));
    }

    private static bool RatioExpression(string expression)
    {
        var tokens = DaxScan.Tokenize(expression);
        return HasRatio(tokens, 0, tokens.Count);
    }

    /// <summary>A constant denominator rescales the average. Resolve variables in the original token scope before deciding.</summary>
    private static bool HasRatio(IReadOnlyList<DaxScan.Tok> tokens, int start, int limit)
    {
        foreach (var open in DaxScan.AllCallOpens(tokens, "DIVIDE"))
        {
            if (open <= start || open >= limit) continue;
            var args = DaxScan.ArgRanges(tokens, open);
            if (args.Count >= 2 && !Constant(tokens, args[1].start, args[1].end)) return true;
        }
        for (var i = start; i < limit; i++)
        {
            if (tokens[i].Kind != DaxScan.Kind.Op || tokens[i].Text != "/") continue;
            var end = i + 1;
            while (end < limit)
            {
                var t = tokens[end];
                if (t.Depth < tokens[i].Depth) break;
                var unary = t.Kind == DaxScan.Kind.Op && t.Text is "+" or "-"
                    && (end == i + 1 || tokens[end - 1].Kind == DaxScan.Kind.Op);
                if (t.Depth == tokens[i].Depth && (t.Kind == DaxScan.Kind.Comma
                    || t.Kind == DaxScan.Kind.Word && (Eq(t.Text, "VAR") || Eq(t.Text, "RETURN"))
                    || t.Kind == DaxScan.Kind.Op && t.Text is "+" or "-" or "*" or "/" && !unary)) break;
                end++;
            }
            if (!Constant(tokens, i + 1, end)) return true;
        }
        return false;
    }

    // A declaration is visible only after its body and inside its enclosing argument or parentheses.
    // Keep original token indexes: slicing an AVERAGEX body loses variables declared outside the iterator.
    private static DaxScan.VarDecl Variable(IReadOnlyList<DaxScan.Tok> tokens, int at)
    {
        if (tokens[at].Kind != DaxScan.Kind.Word) return null;
        foreach (var v in DaxScan.VarDecls(tokens).AsEnumerable().Reverse())
        {
            if (!Eq(v.Name, tokens[at].Text) || v.BodyEnd > at) continue;
            var depth = tokens[v.NameIndex].Depth;
            var visible = true;
            for (var i = v.NameIndex + 1; i < at; i++)
                if (tokens[i].Depth < depth || tokens[i].Depth == depth && tokens[i].Kind == DaxScan.Kind.Comma)
                { visible = false; break; }
            if (visible) return v;
        }
        return null;
    }

    private static readonly HashSet<string> ConstantFunctions = new(StringComparer.OrdinalIgnoreCase)
        { "ABS", "ROUND", "ROUNDUP", "ROUNDDOWN", "INT", "TRUNC", "POWER", "SQRT", "MOD", "DIVIDE", "MIN", "MAX", "CONVERT" };

    private static bool Constant(IReadOnlyList<DaxScan.Tok> tokens, int start, int end)
    {
        if (start >= end) return false;
        for (var i = start; i < end; i++)
        {
            var t = tokens[i];
            if (t.Kind == DaxScan.Kind.Name) return false;
            if (t.Kind != DaxScan.Kind.Word) continue;
            if (i + 1 < end && tokens[i + 1].Kind == DaxScan.Kind.LParen)
            {
                if (!ConstantFunctions.Contains(t.Text)) return false;
                continue;
            }
            var v = Variable(tokens, i);
            if (v == null || !Constant(tokens, v.BodyStart, v.BodyEnd)) return false;
        }
        return true;
    }

    internal static bool AverageOfRatio(Measure measure)
    {
        var tokens = DaxScan.Tokenize(measure.Expression);
        foreach (var open in DaxScan.AllCallOpens(tokens, "AVERAGEX"))
        {
            var args = DaxScan.ArgRanges(tokens, open);
            if (args.Count != 2) continue;
            var range = args[1];
            if (HasRatio(tokens, range.start, range.end)) return true;
            for (var i = range.start; i < range.end; i++)
                if (Reference(measure, tokens, i) is Measure referenced && RatioExpression(referenced.Expression)) return true;
        }
        foreach (var open in DaxScan.AllCallOpens(tokens, "AVERAGE"))
        {
            var args = DaxScan.ArgRanges(tokens, open);
            if (args.Count != 1) continue;
            for (var i = args[0].start; i < args[0].end; i++)
                if (Reference(measure, tokens, i) is Column column &&
                    ((column.FormatString ?? "").Contains('%') || RatioName(column.Name)
                    || column is CalculatedColumn calc && RatioExpression(calc.Expression))) return true;
        }
        return false;
    }

    private static bool RatioName(string name) => name.Contains('%') || Words(name).Any(w => w is "ratio" or "percent" or "percentage" or "pct");

    /// <summary>Offer only a whole-expression average over a physical table and linear column operands.
    /// Filters, variables, iterated measures and other shapes need the assistant to author a candidate.</summary>
    internal static string RatioOfTotals(Measure measure)
    {
        var tokens = DaxScan.Tokenize(measure.Expression);
        IReadOnlyList<DaxScan.Tok> row = null;
        Table table = null;
        if (DaxScan.RootCallName(tokens, 0, tokens.Count) == "AVERAGEX")
        {
            var args = DaxScan.ArgRanges(tokens, 1);
            if (args.Count != 2 || args[0].end - args[0].start != 1) return null;
            var t = tokens[args[0].start];
            table = measure.Model.Tables.FirstOrDefault(x => Eq(x.Name, t.Inner ?? t.Text));
            row = tokens.Skip(args[1].start).Take(args[1].end - args[1].start).ToArray();
        }
        else if (DaxScan.RootCallName(tokens, 0, tokens.Count) == "AVERAGE")
        {
            var args = DaxScan.ArgRanges(tokens, 1);
            if (args.Count != 1) return null;
            var index = args[0].end - 1;
            if (Reference(measure, tokens, index) is not CalculatedColumn c) return null;
            table = c.Table;
            row = DaxScan.Tokenize(c.Expression);
        }
        if (table == null || row == null) return null;
        List<(int start, int end)> operands;
        // With "/" a plus or minus beside the division belongs to a different term (a / b + c is not a / (b + c)), so only DIVIDE's own arguments may hold a sum.
        var sumsAllowed = DaxScan.RootCallName(row, 0, row.Count) == "DIVIDE";
        if (sumsAllowed)
        {
            operands = DaxScan.ArgRanges(row, 1);
            if (operands.Count != 2) return null; // A third argument chooses different blank/zero behaviour.
        }
        else
        {
            var divisions = DaxScan.TopLevelOps(row, 0, row.Count).Where(x => x.op == "/").ToArray();
            if (divisions.Length != 1) return null;
            operands = new() { (0, divisions[0].index), (divisions[0].index + 1, row.Count) };
        }
        string Total((int start, int end) range)
        {
            var output = new StringBuilder();
            var needsColumn = true;
            for (var i = range.start; i < range.end; i++)
            {
                if (!needsColumn && row[i].Kind == DaxScan.Kind.Op && row[i].Text is "+" or "-")
                {
                    if (!sumsAllowed) return null;
                    output.Append(" " + row[i].Text + " "); needsColumn = true; continue;
                }
                if (!needsColumn) return null;
                if (i + 1 < range.end && row[i + 1].Kind == DaxScan.Kind.Name && row[i + 1].Delim == '[')
                {
                    if (!Eq(row[i].Inner ?? row[i].Text, table.Name)) return null;
                    i++;
                }
                else if (measure.Model.AllMeasures.Any(m => Eq(m.Name, row[i].Inner))) return null;
                if (row[i].Kind != DaxScan.Kind.Name || row[i].Delim != '[') return null;
                var column = table.Columns.FirstOrDefault(c => Eq(c.Name, row[i].Inner));
                if (column == null) return null;
                output.Append("SUM(" + ColumnDax(column) + ")"); needsColumn = false;
            }
            return needsColumn ? null : output.ToString();
        }
        var numerator = Total(operands[0]);
        var denominator = Total(operands[1]);
        return numerator == null || denominator == null ? null : $"DIVIDE({numerator}, {denominator})";
    }

    private static IEnumerable<string> Words(string name)
    {
        var word = new StringBuilder();
        foreach (var c in name ?? "")
        {
            if (!char.IsLetter(c) || char.IsUpper(c) && word.Length > 0 && char.IsLower(word[word.Length - 1]))
            { if (word.Length > 0) { yield return word.ToString().ToLowerInvariant(); word.Clear(); } }
            if (char.IsLetter(c)) word.Append(c);
        }
        if (word.Length > 0) yield return word.ToString().ToLowerInvariant();
    }

    private static bool SnapshotName(string name) => Words(name).Any(w => w is "balance" or "inventory" or "stock" or "headcount" or "closing")
        || (name ?? "").Replace(" ", "").Replace("_", "").IndexOf("onhand", StringComparison.OrdinalIgnoreCase) >= 0;

    private static readonly string[] ClosingFunctions = { "LASTDATE", "LASTNONBLANK", "LASTNONBLANKVALUE",
        "CLOSINGBALANCEMONTH", "CLOSINGBALANCEQUARTER", "CLOSINGBALANCEYEAR",
        "OPENINGBALANCEMONTH", "OPENINGBALANCEQUARTER", "OPENINGBALANCEYEAR" };

    private static bool Protected(Measure measure, IReadOnlyList<DaxScan.Tok> tokens, int index)
    {
        foreach (var function in ClosingFunctions)
            foreach (var open in DaxScan.AllCallOpens(tokens, function))
                if (open < index && DaxScan.MatchingRParen(tokens, open) > index) return true;
        foreach (var open in DaxScan.AllCallOpens(tokens, "CALCULATE"))
        {
            var args = DaxScan.ArgRanges(tokens, open);
            if (args.Count < 2 || index < args[0].start || index >= args[0].end) continue;
            if (args.Skip(1).Any(a => OneDateFilter(measure, tokens, a.start, a.end))) return true;
        }
        return false;
    }

    private static void Unwrap(IReadOnlyList<DaxScan.Tok> tokens, ref int start, ref int end)
    {
        while (start < end && tokens[start].Kind == DaxScan.Kind.LParen && DaxScan.MatchingRParen(tokens, start) == end - 1)
        { start++; end--; }
    }

    private static bool DateColumn(Measure measure, IReadOnlyList<DaxScan.Tok> tokens, int start, int end)
    {
        Unwrap(tokens, ref start, ref end);
        return end > start && end - start <= 2 && tokens[end - 1].Kind == DaxScan.Kind.Name
            && (end - start == 1 || tokens[start].Kind == DaxScan.Kind.Word || tokens[start].Kind == DaxScan.Kind.Name)
            && Reference(measure, tokens, end - 1) is Column c && c.DataType == DataType.DateTime;
    }

    private static bool SingleValue(Measure measure, IReadOnlyList<DaxScan.Tok> tokens, int start, int end, bool allowMeasure = false)
    {
        Unwrap(tokens, ref start, ref end);
        if (end <= start) return false;
        // A predicate's measure can change per date; a variable keeps its previously evaluated value.
        if (end - start == 1 && Variable(tokens, start) is { } v)
            return SingleValue(measure, tokens, v.BodyStart, v.BodyEnd, allowMeasure: true);
        if (Constant(tokens, start, end)) return true;
        if (end - start <= 2 && Reference(measure, tokens, end - 1) is Measure) return allowMeasure;
        var root = DaxScan.RootCallName(tokens, start, end);
        if (root is "TODAY" or "NOW") return true;
        if (root != null)
        {
            var args = DaxScan.ArgRanges(tokens, start + 1);
            if (root is "MAX" or "MIN" or "SELECTEDVALUE" or "LASTDATE" or "FIRSTDATE" or "LASTNONBLANK" or "FIRSTNONBLANK"
                && args.Count > 0 && DateColumn(measure, tokens, args[0].start, args[0].end)) return true;
            // A scalar wrapper preserves a fixed value only when its inputs are fixed too.
            // DATE(YEAR(Date[Date]), 12, 31) varies by row and can select several year ends.
            // CALCULATE can turn the current FILTER row into a different context for each date.
            return root is not ("CALCULATE" or "CALCULATETABLE") && args.Count > 0
                && args.All(a => SingleValue(measure, tokens, a.start, a.end, allowMeasure));
        }
        var ops = DaxScan.TopLevelOps(tokens, start, end);
        if (ops.Count > 0 && ops.All(o => o.op is "+" or "-" or "*" or "/"))
        {
            var op = ops.Last().index;
            return SingleValue(measure, tokens, start, op, allowMeasure) && SingleValue(measure, tokens, op + 1, end, allowMeasure);
        }
        return false;
    }

    // A date column equal to one value restricts the calculation to at most one date.
    // FILTER and KEEPFILTERS keep that restriction; an OR or a range can admit several dates.
    private static bool OneDateFilter(Measure measure, IReadOnlyList<DaxScan.Tok> tokens, int start, int end)
    {
        Unwrap(tokens, ref start, ref end);
        if (end <= start) return false;
        if (end - start == 1 && Variable(tokens, start) is { } v)
            return OneDateFilter(measure, tokens, v.BodyStart, v.BodyEnd);
        var root = DaxScan.RootCallName(tokens, start, end);
        if (root is "FILTER" or "KEEPFILTERS")
        {
            var args = DaxScan.ArgRanges(tokens, start + 1);
            var a = root == "FILTER" ? 1 : 0;
            return args.Count > a && OneDateFilter(measure, tokens, args[a].start, args[a].end);
        }
        if (root is "LASTDATE" or "FIRSTDATE" or "LASTNONBLANK" or "FIRSTNONBLANK")
        {
            var args = DaxScan.ArgRanges(tokens, start + 1);
            return args.Count > 0 && DateColumn(measure, tokens, args[0].start, args[0].end);
        }
        var ops = DaxScan.TopLevelOps(tokens, start, end);
        if (ops.Any(o => o.op == "||")) return false;
        var and = ops.FirstOrDefault(o => o.op == "&&");
        if (and != default)
            return OneDateFilter(measure, tokens, start, and.index) || OneDateFilter(measure, tokens, and.index + 1, end);
        var equal = ops.Where(o => o.op is "=" or "==").ToArray();
        if (equal.Length != 1) return false;
        var split = equal[0].index;
        return DateColumn(measure, tokens, start, split) && SingleValue(measure, tokens, split + 1, end)
            || SingleValue(measure, tokens, start, split) && DateColumn(measure, tokens, split + 1, end);
    }

    private static (bool applies, bool unsafeSum) SnapshotSum(Measure measure, HashSet<Measure> path)
    {
        if (!path.Add(measure)) return (false, false);
        var applies = false;
        var unsafeSum = false;
        var tokens = DaxScan.Tokenize(measure.Expression);
        foreach (var open in DaxScan.AllCallOpens(tokens, "SUM"))
        {
            var args = DaxScan.ArgRanges(tokens, open);
            if (args.Count != 1) continue;
            for (var i = args[0].start; i < args[0].end; i++)
                if (Reference(measure, tokens, i) is Column c && (SnapshotName(c.Name) || SnapshotName(measure.Name)))
                { applies = true; unsafeSum |= !Protected(measure, tokens, open); }
        }
        for (var i = 0; i < tokens.Count; i++)
            if (Reference(measure, tokens, i) is Measure dependency)
            {
                var nested = SnapshotSum(dependency, path);
                applies |= nested.applies;
                unsafeSum |= nested.unsafeSum && !Protected(measure, tokens, i);
            }
        path.Remove(measure);
        return (applies, unsafeSum);
    }

    private static IEnumerable<(int start, int end)> FilterRanges(IReadOnlyList<DaxScan.Tok> tokens)
    {
        foreach (var function in new[] { "CALCULATE", "CALCULATETABLE", "FILTER" })
            foreach (var open in DaxScan.AllCallOpens(tokens, function))
            {
                var args = DaxScan.ArgRanges(tokens, open);
                foreach (var range in args.Skip(1)) yield return range;
            }
    }

    private static bool IsDateReference(Measure measure, IReadOnlyList<DaxScan.Tok> tokens, int i) =>
        Reference(measure, tokens, i) is Column column && (column.DataType == DataType.DateTime || Words(column.Name).Contains("year"))
        || tokens[i].Kind == DaxScan.Kind.Name && tokens[i].Delim == '[' && Words(tokens[i].Inner).Any(w => w is "date" or "year");

    private static bool HasDateInput(Measure measure, IReadOnlyList<DaxScan.Tok> tokens, int start, int end) =>
        Enumerable.Range(start, end - start).Any(i => IsDateReference(measure, tokens, i))
        || DaxScan.CallsFunction(tokens.Skip(start).Take(end - start).ToArray(), "DATE");

    private static bool HasFixedDate(Measure measure, IReadOnlyList<DaxScan.Tok> tokens, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (tokens[i].Kind == DaxScan.Kind.Word && Eq(tokens[i].Text, "DATE") && i + 1 < end)
            {
                var args = DaxScan.ArgRanges(tokens, i + 1);
                if (args.Count == 3 && args[0].end - args[0].start == 1 && tokens[args[0].start].Kind == DaxScan.Kind.Number) return true;
            }
            if (tokens[i].Kind != DaxScan.Kind.Op || tokens[i].Text is not ("=" or "==" or "<" or ">" or "<=" or ">=" or "<>")) continue;
            bool Literal(int at) => at >= start && at < end &&
                (tokens[at].Kind == DaxScan.Kind.Number && int.TryParse(tokens[at].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y >= 1900 && y <= 9999
                || tokens[at].Kind == DaxScan.Kind.String && DateTime.TryParseExact(tokens[at].Inner, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            if (i > start && IsDateReference(measure, tokens, i - 1) && Literal(i + 1)) return true;
            if (Literal(i + 1) && DaxScan.AllCallOpens(tokens, "YEAR").Any(open =>
                DaxScan.MatchingRParen(tokens, open) == i - 1 && open >= start)) return true;
            if (i + 1 < end && IsDateReference(measure, tokens, i + 1) && Literal(i - 1)) return true;
        }
        return false;
    }
}
