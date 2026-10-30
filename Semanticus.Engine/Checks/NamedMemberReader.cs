#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Analysis;

namespace Semanticus.Engine.Checks;

/// <summary>The members of one column that a formula names as a literal.</summary>
public record NamedMembers(Axis Axis, KeyPart[] Keys);

/// <summary>Find literal column members in a formula so context probes can include those members.</summary>
public static class NamedMemberReader
{
    private static readonly string[] Wrappers = { "SELECTEDVALUE", "VALUES", "DISTINCT", "MIN", "MAX" };
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) {
        "RETURN", "VAR", "IN", "NOT", "AND", "OR", "DEFINE", "EVALUATE", "ORDER", "BY", "ASC", "DESC", "START", "AT"
    };

    public static NamedMembers[] For(ModelSnapshot snapshot, IEnumerable<Axis> axes)
    {
        var wanted = axes.ToArray();
        var found = new List<(Axis Axis, string Text, bool Number)>();
        foreach (var measure in snapshot.Dependencies.Append(snapshot.Candidate))
            Scan(DaxScan.Tokenize(measure.Expression), found);
        var result = new Dictionary<Axis, List<KeyPart>>();
        foreach (var (reference, text, number) in found)
        {
            var axis = wanted.FirstOrDefault(a => ModelSnapshots.Eq(a.Table, reference.Table) && ModelSnapshots.Eq(a.Column, reference.Column));
            var column = axis == null ? null : snapshot.Structure.Columns.FirstOrDefault(c => c.Axis == axis);
            if (axis == null || column == null || !Key(column.Kind, text, number, out var key)) continue;
            if (!result.TryGetValue(axis, out var keys)) result[axis] = keys = new List<KeyPart>();
            if (!keys.Contains(key)) keys.Add(key);
        }
        return result.Select(p => new NamedMembers(p.Key, p.Value.ToArray())).ToArray();
    }

    // A literal of the wrong type for its column (text against a number) is a formula error the server reports itself.
    private static bool Key(ColumnKind kind, string text, bool number, out KeyPart key)
    {
        key = new KeyPart(KeyKind.Value, text);
        switch (kind)
        {
            case ColumnKind.Text: return !number;
            case ColumnKind.Integer:
                if (!number || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) return false;
                key = new KeyPart(KeyKind.Value, whole.ToString(CultureInfo.InvariantCulture)); return true;
            case ColumnKind.Decimal:
                if (!number || !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)) return false;
                key = new KeyPart(KeyKind.Value, exact.ToString(null, CultureInfo.InvariantCulture)); return true;
            case ColumnKind.Double:
                if (!number || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) || !double.IsFinite(real)) return false;
                key = new KeyPart(KeyKind.Value, real.ToString("R", CultureInfo.InvariantCulture)); return true;
            default: return false;
        }
    }

    private static bool Literal(IReadOnlyList<DaxScan.Tok> tokens, int at, out string text, out bool number, out int end)
    {
        text = ""; number = false; end = at + 1;
        if (at < 0 || at >= tokens.Count) return false;
        var token = tokens[at];
        if (token.Kind == DaxScan.Kind.String) { text = token.Inner; return true; }
        if (token.Kind == DaxScan.Kind.Number) { text = token.Text; number = true; return true; }
        if (token.Kind == DaxScan.Kind.Op && token.Text == "-" && at + 1 < tokens.Count && tokens[at + 1].Kind == DaxScan.Kind.Number)
        { text = "-" + tokens[at + 1].Text; number = true; end = at + 2; return true; }
        return false;
    }

    private static bool IsEquals(DaxScan.Tok token) => token.Kind == DaxScan.Kind.Op && token.Text is "=" or "==" or "<>";

    private static void Scan(List<DaxScan.Tok> tokens, List<(Axis, string, bool)> found)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != DaxScan.Kind.Name || tokens[i].Delim != '[' || i == 0) continue;
            var previous = tokens[i - 1];
            var table = previous.Kind == DaxScan.Kind.Word && !Keywords.Contains(previous.Text) ? previous.Text
                : previous.Kind == DaxScan.Kind.Name && previous.Delim == '\'' ? previous.Inner : "";
            if (table.Length == 0) continue;
            var axis = new Axis(table, tokens[i].Inner);
            var start = i - 1;
            var end = i + 1;
            var call = -1;
            // SELECTEDVALUE(col), VALUES(col): the operand is the whole call.
            if (start >= 2 && tokens[start - 1].Kind == DaxScan.Kind.LParen && tokens[start - 2].Kind == DaxScan.Kind.Word
                && Wrappers.Any(w => string.Equals(w, tokens[start - 2].Text, StringComparison.OrdinalIgnoreCase)))
            {
                call = start - 2;
                var close = DaxScan.MatchingRParen(tokens, start - 1);
                if (close < 0) continue;
                start = call; end = close + 1;
            }
            if (end < tokens.Count && IsEquals(tokens[end]) && Literal(tokens, end + 1, out var text, out var number, out _))
                found.Add((axis, text, number));
            else if (end + 1 < tokens.Count && tokens[end].Kind == DaxScan.Kind.Word
                && string.Equals(tokens[end].Text, "IN", StringComparison.OrdinalIgnoreCase)
                && tokens[end + 1].Kind == DaxScan.Kind.LBrace)
            {
                for (var at = end + 2; at < tokens.Count && tokens[at].Kind != DaxScan.Kind.RBrace;)
                {
                    if (Literal(tokens, at, out var item, out var itemNumber, out var next)) { found.Add((axis, item, itemNumber)); at = next; }
                    else at++;
                }
            }
            if (start >= 2 && IsEquals(tokens[start - 1]) && Literal(tokens, start - 2, out var left, out var leftNumber, out _))
                found.Add((axis, left, leftNumber));
            // SWITCH(SELECTEDVALUE(col), "a", result, "b", result, default): the odd arguments are the members.
            if (call >= 2 && string.Equals(tokens[call - 2].Text, "SWITCH", StringComparison.OrdinalIgnoreCase)
                && tokens[call - 1].Kind == DaxScan.Kind.LParen)
            {
                var args = DaxScan.ArgRanges(tokens, call - 1);
                for (var a = 1; a + 1 < args.Count; a += 2)
                    if (Literal(tokens, args[a].start, out var option, out var optionNumber, out var stop) && stop == args[a].end)
                        found.Add((axis, option, optionNumber));
            }
        }
    }
}
