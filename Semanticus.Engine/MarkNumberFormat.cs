using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Semanticus.Engine;

/// <summary>
/// Every number a mark shows, formatted once, here, the way Power BI shows it: the value's shortest round-trip text, read as an
/// exact decimal, then the measure's format string (custom, named and currency forms). A double is never rounded in binary, so
/// 1234.4999999999998 with "#,0" is 1,234 everywhere. General groups the integer digits, as the cards have since fx19; a custom
/// format groups them only when it has a grouping comma, as Power BI does ("0.0##" shows 1234.5, "#,0.0" 1,234.5). A format this
/// reader does not fully understand (a condition, two digit runs, a DAX expression) is not guessed at: the number shows in
/// General, at most 15 significant digits. The Filters sentence and every mark cell use this class; the webview
/// prints the text it returns.
/// </summary>
internal static class MarkNumberFormat
{
    /// <summary>
    /// One number, such as a Filters total or a cell value. No value is "Blank". Anything else that is not a number (a date, an
    /// error text) comes back as the engine's own text for it (<see cref="DaxBench.Fmt"/>), so a date stays ISO for the
    /// caller's date rule.
    /// </summary>
    internal static string Display(object value, string format)
    {
        if (value == null || value is DBNull) return "Blank";
        var number = Read(value);
        return number == null ? DaxBench.Fmt(value) : Render(number.Value, format, 0);
    }

    /// <summary>
    /// One value as a mark cell or sentence shows it. A date under the measure's own date format reads in that format
    /// ("yyyy-mm-dd" shows 2016-09-12, rc28 tester gap 8); a date with no date format keeps the words rule (12 Sep 2016); a
    /// number under a date format is not a date, so it reads in General; everything else is <see cref="Display"/>.
    /// </summary>
    internal static string DisplayText(object value, string format)
    {
        var pattern = DatePattern(format);
        if (pattern != null && AsDate(value) is { } date) return date.ToString(pattern, CultureInfo.InvariantCulture);
        if (pattern != null && Read(value) is { } number) return Render(number, null, 0);
        return MeasureMarkJudge.CellText(Display(value, format));
    }

    // A value a probe or a saved cell holds as a date: the DateTime itself, its ISO text, or the words a cell was given.
    private static readonly string[] DateTexts = { "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd", "d MMM yyyy", "d MMM yyyy HH:mm", "d MMM yyyy HH:mm:ss.FFFFFFF" };
    private static DateTime? AsDate(object value) => value switch
    {
        DateTime d => d,
        DateTimeOffset o => o.DateTime,
        string s when DateTime.TryParseExact(s.Trim(), DateTexts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        string s when Regex.IsMatch(s.Trim(), @"^\d{4}-\d{2}-\d{2}T") && DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var o) => o.DateTime,
        _ => null,
    };

    /// <summary>
    /// The .NET pattern for a measure's custom date format, or null when the format is not a date format. Power BI reads
    /// date formats the Visual Basic way: m is the month unless it follows an hour or comes before seconds (then minutes), n
    /// is minutes, AM/PM makes the hours 12-hour. A number placeholder (0, #, %) means a number format. A named format
    /// (Short Date, General Date) follows the reader's locale, which the engine does not know, so it keeps the words rule.
    /// </summary>
    internal static string DatePattern(string format)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;
        var parts = new List<(char Kind, int Length, string Literal)>();
        var twelve = format.Contains("AM/PM", StringComparison.OrdinalIgnoreCase) || format.Contains("A/P", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '"')
            {
                var end = format.IndexOf('"', i + 1);
                if (end < 0) return null;
                parts.Add(('L', 0, format.Substring(i + 1, end - i - 1))); i = end; continue;
            }
            if (c == (char)92) { if (i + 1 < format.Length) parts.Add(('L', 0, format[++i].ToString())); continue; }
            if (c == '[')
            {
                // [$-en-US] or [$-409] is the locale Power BI writes before a date format: not part of the date (fx29b I).
                // Any other bracket (a currency [$€-407], elapsed [h], a colour) is not a date this reads.
                var end = format.IndexOf(']', i + 1);
                if (end < 0 || !format.AsSpan(i + 1).StartsWith("$-")) return null;
                i = end; continue;
            }
            if (string.Compare(format, i, "AM/PM", 0, 5, StringComparison.OrdinalIgnoreCase) == 0) { parts.Add(('t', 2, null)); i += 4; continue; }
            if (string.Compare(format, i, "A/P", 0, 3, StringComparison.OrdinalIgnoreCase) == 0) { parts.Add(('t', 1, null)); i += 2; continue; }
            var lower = char.ToLowerInvariant(c);
            if ("ymdhns".Contains(lower))
            {
                var length = 1;
                while (i + 1 < format.Length && char.ToLowerInvariant(format[i + 1]) == lower) { i++; length++; }
                parts.Add((lower, length, null));
                continue;
            }
            if (c is '0' or '#' or '%' or '@' or ';' || char.IsLetter(c)) return null;
            parts.Add(('L', 0, c.ToString()));
        }
        if (!parts.Any(p => p.Kind is 'y' or 'd' or 'm')) return null;
        var pattern = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            var (kind, length, literal) = parts[i];
            var previous = parts.Take(i).LastOrDefault(p => p.Kind != 'L').Kind;
            var next = parts.Skip(i + 1).FirstOrDefault(p => p.Kind != 'L').Kind;
            pattern.Append(kind switch
            {
                'y' => length >= 3 ? "yyyy" : "yy",
                'm' when previous == 'h' || next == 's' => length >= 2 ? "mm" : "m",
                'm' => length switch { 1 => "M", 2 => "MM", 3 => "MMM", _ => "MMMM" },
                'd' => length switch { 1 => "d", 2 => "dd", 3 => "ddd", _ => "dddd" },
                'h' => twelve ? (length >= 2 ? "hh" : "h") : (length >= 2 ? "HH" : "H"),
                'n' => length >= 2 ? "mm" : "m",
                's' => length >= 2 ? "ss" : "s",
                't' => length >= 2 ? "tt" : "t",
                _ => string.Concat(literal.Select(ch => char.IsLetterOrDigit(ch) || ch is '\'' or '"' or '%' or (char)92 ? (char)92 + ch.ToString() : ch.ToString())),
            });
        }
        // One letter alone is a .NET standard format ("d" is the short date), so it is marked as a custom one.
        return pattern.Length == 1 ? "%" + pattern : pattern.ToString();
    }

    /// <summary>
    /// The Before and After of one Equivalence place, each in its own format. When two different values would read the same
    /// (a tiny move under "#,0", or 0 and 0.00003 with no format), both get more decimals until they read differently.
    /// </summary>
    internal static (string Before, string After) DisplayPair(object before, object after, string beforeFormat, string afterFormat)
    {
        var shown = (Before: Display(before, beforeFormat), After: Display(after, afterFormat));
        var (b, a) = (before == null || before is DBNull ? null : Read(before), after == null || after is DBNull ? null : Read(after));
        if (b == null || a == null || b.Value == a.Value) return shown;
        for (var extra = 1; shown.Before == shown.After && extra <= 400; extra++)
            shown = (Render(b.Value, beforeFormat, extra), Render(a.Value, afterFormat, extra));
        return shown;
    }

    /// <summary>
    /// The display text of every cell of a mark, set once: each Equivalence place as a pair in its own before and after format,
    /// a row count (the cells of a Filters warning) in General, a timing as the engine wrote it, and every other value in the
    /// measure's format. A cell that already has its text keeps it, so the run that made the mark decides.
    /// </summary>
    internal static MeasureMark FillCells(MeasureMark mark, string format)
    {
        if (mark?.Cells == null || mark.Name == "Performance") return mark;
        var counts = mark.Name == "Filters" && mark.State == "suspect";
        foreach (var cell in mark.Cells.Where(c => c.BeforeText == null && c.AfterText == null))
        {
            var (before, after) = (Value(cell.Before), Value(cell.After));
            if (mark.Name == "Equivalence" && (AsDate(before) ?? AsDate(after)) != null)
                (cell.BeforeText, cell.AfterText) = (Shown(cell.Before, before, mark.BeforeFormatString), Shown(cell.After, after, mark.AfterFormatString));
            else if (mark.Name == "Equivalence")
            {
                (cell.BeforeText, cell.AfterText) = DisplayPair(before, after, mark.BeforeFormatString, mark.AfterFormatString);
                cell.BeforeText = MeasureMarkJudge.CellText(cell.BeforeText);
                cell.AfterText = MeasureMarkJudge.CellText(cell.AfterText);
            }
            else (cell.BeforeText, cell.AfterText) = (Shown(cell.Before, before, counts ? null : format), Shown(cell.After, after, counts ? null : format));
        }
        // A record saved before the gap text gets it here too: it needs only the two values and the format.
        foreach (var cell in mark.Cells.Where(c => c.GapText == null && !counts))
            cell.GapText = Gap(Value(cell.Before), Value(cell.After), mark.Name == "Equivalence" ? mark.AfterFormatString : format);
        return mark;
    }

    /// <summary>
    /// The gap between two values, |after - before|, worked as an exact decimal and shown in the measure's format, so the gap
    /// between $1,000.00 and -$5.00 reads $1,005.00. A gap the format would round to nothing gets more places until it shows
    /// ("$0.001", never "$0.00"), so a gap never reads 0. Null when either value is not a number or the two are the same.
    /// </summary>
    internal static string Gap(object before, object after, string format)
    {
        var (b, a) = (Exact(before), Exact(after));
        if (b == null || a == null || b == a) return null;
        var gap = Read(Math.Abs(a.Value - b.Value)).Value;
        for (var extra = 0; extra <= 28; extra++)
        {
            var text = Render(gap, format, extra);
            if (text.Any(c => c is >= '1' and <= '9')) return text;
        }
        return General(gap, 0);
    }

    // The value's round-trip text as a decimal, so a subtraction adds no binary noise. Null past decimal's range.
    private static decimal? Exact(object value)
    {
        var text = value switch
        {
            double d => double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : null,
            float f => float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : null,
            string s => NumberText.IsMatch(s ?? "") ? s : null,
            null or DBNull => null,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
        return decimal.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : null;
    }

    // A cell holds the engine's text of a value: "BLANK" for no value, else the number's round-trip text, a date or a message.
    private static object Value(string text) => text == "BLANK" ? null : text;
    private static string Shown(string text, object value, string format) => text == null ? null : DisplayText(value, format);

    // ---- the exact decimal ------------------------------------------------------------------------------------------------------

    /// <summary>A decimal number: 0.Digits times 10 to the Point. Digits has no leading or trailing zero; zero has no digits.</summary>
    private readonly record struct Dec(bool Negative, string Digits, int Point)
    {
        internal bool Zero => Digits.Length == 0;
        internal Dec Abs => this with { Negative = false };
        internal Dec Shift(int places) => Zero ? this : this with { Point = Point + places };
    }

    private static readonly Regex NumberText = new(@"^\s*(?<sign>[+-])?(?<int>\d*)(?:\.(?<frac>\d*))?(?:[eE](?<exp>[+-]?\d{1,6}))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Dec? Read(object value) => value switch
    {
        double d => double.IsFinite(d) ? Parse(d.ToString("R", CultureInfo.InvariantCulture)) : null,
        float f => float.IsFinite(f) ? Parse(f.ToString("R", CultureInfo.InvariantCulture)) : null,
        decimal m => Parse(m.ToString(CultureInfo.InvariantCulture)),
        long or int or short or sbyte or ulong or uint or ushort or byte => Parse(Convert.ToString(value, CultureInfo.InvariantCulture)),
        string s => Parse(s),
        _ => null,
    };

    private static Dec? Parse(string text)
    {
        var match = NumberText.Match(text ?? "");
        if (!match.Success || match.Groups["int"].Length + match.Groups["frac"].Length == 0) return null;
        var digits = match.Groups["int"].Value + match.Groups["frac"].Value;
        var point = match.Groups["int"].Length + (match.Groups["exp"].Success ? int.Parse(match.Groups["exp"].Value, CultureInfo.InvariantCulture) : 0);
        var lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.Trim('0');
        return digits.Length == 0 ? new Dec(false, "", 0) : new Dec(match.Groups["sign"].Value == "-", digits, point - lead);
    }

    /// <summary>The integer and fraction digits of |d| rounded half away from zero to <paramref name="fraction"/> places.</summary>
    private static (string Int, string Frac) Round(Dec d, int fraction)
    {
        string whole, part;
        if (d.Zero) (whole, part) = ("0", "");
        else if (d.Point >= d.Digits.Length) (whole, part) = (d.Digits + new string('0', d.Point - d.Digits.Length), "");
        else if (d.Point > 0) (whole, part) = (d.Digits[..d.Point], d.Digits[d.Point..]);
        else (whole, part) = ("0", new string('0', -d.Point) + d.Digits);
        if (part.Length <= fraction) return (whole, part.PadRight(fraction, '0'));
        var kept = new StringBuilder(whole + part[..fraction]);
        if (part[fraction] >= '5')
        {
            var i = kept.Length - 1;
            for (; i >= 0 && kept[i] == '9'; i--) kept[i] = '0';
            if (i < 0) kept.Insert(0, '1'); else kept[i]++;
        }
        var text = kept.ToString();
        var integer = text[..^fraction].TrimStart('0');
        return (integer.Length == 0 ? "0" : integer, text[^fraction..]);
    }

    private static string Group(string integer) => Regex.Replace(integer, @"\B(?=(\d{3})+$)", ",");

    // ---- General: no format, "General Number", or a format this reader does not read ---------------------------------------------

    private const int GeneralDigits = 15;

    private static string General(Dec d, int extra)
    {
        if (d.Zero) return "0";
        var digits = GeneralDigits + extra;
        var sign = d.Negative ? "-" : "";
        // Below 1E-15 Power BI's General shows an exponent; a plain decimal would be hundreds of zeros long.
        if (d.Point < -14)
        {
            var (mantissa, exponent) = Mantissa(d.Abs, 1, digits - 1);
            return sign + Trimmed(mantissa.Int, mantissa.Frac, 0) + "E-" + (-exponent).ToString(CultureInfo.InvariantCulture);
        }
        var (integer, frac) = Round(d.Abs, Math.Max(0, digits - d.Point));
        var text = Trimmed(Group(integer), frac, 0);
        return text == "0" ? "0" : sign + text;
    }

    private static string Trimmed(string integer, string frac, int keep)
    {
        var shown = frac.Length > keep ? frac.TrimEnd('0').PadRight(keep, '0') : frac;
        return shown.Length == 0 ? integer : integer + "." + shown;
    }

    /// <summary>|d| as a mantissa with <paramref name="integerDigits"/> digits before the point, and the power of ten it needs.</summary>
    private static ((string Int, string Frac) Mantissa, int Exponent) Mantissa(Dec d, int integerDigits, int fraction)
    {
        if (d.Zero) return (Round(d, fraction), 0);
        var exponent = d.Point - integerDigits;
        var mantissa = Round(d with { Point = integerDigits }, fraction);
        if (mantissa.Int.Length > integerDigits) { exponent++; mantissa = Round(d with { Point = integerDigits - 1 }, fraction); }
        return (mantissa, exponent);
    }

    // ---- named formats -------------------------------------------------------------------------------------------------------

    // Power BI's predefined numeric formats (the FORMAT function's names), in the en-US forms the cards use.
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Currency"] = "$#,0.00;($#,0.00)", ["Fixed"] = "0.00", ["Standard"] = "#,0.00", ["Percent"] = "0.00%", ["Scientific"] = "0.00E+00",
    };
    private static readonly Dictionary<string, (string Yes, string No)> Logical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Yes/No"] = ("Yes", "No"), ["True/False"] = ("True", "False"), ["On/Off"] = ("On", "Off"),
    };

    private static string Render(Dec d, string format, int extra)
    {
        var name = format?.Trim() ?? "";
        if (name.Length == 0 || name.Equals("General Number", StringComparison.OrdinalIgnoreCase) || name.Equals("General", StringComparison.OrdinalIgnoreCase))
            return General(d, extra);
        if (Logical.TryGetValue(name, out var words)) return d.Zero ? words.No : words.Yes;
        return Custom(d, Named.TryGetValue(name, out var named) ? named : format, extra) ?? General(d, extra);
    }

    // ---- custom formats ------------------------------------------------------------------------------------------------------

    private abstract record Tok;
    private sealed record Lit(string Text) : Tok;
    private sealed record Pct : Tok;
    private sealed record Num(string Run) : Tok;

    // What may stand unquoted next to the digits, as in Excel's own rules: currency signs, signs, brackets, a slash, a colon, a space.
    private const string SafeLiteral = "$€£¥-+()/: ";
    private static readonly Regex Locale = new(@"^\$(?<sign>[^\]-]*)(?:-[\w-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Colour = new(@"^(?:Black|Blue|Cyan|Green|Magenta|Red|White|Yellow|Color\d{1,2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Run = new(@"^(?<int>[#0,]*)(?:\.(?<dec>[#0]*))?(?:(?<e>[Ee][+-])(?<exp>[#0]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string Custom(Dec d, string format, int extra)
    {
        var sections = SectionsOf(format);
        if (sections == null) return null;
        var read = sections.Select(TokensOf).ToArray();
        // A format with no digit placeholder anywhere is not a number format (a quoted DAX string, a word): show the number.
        if (read.Any(t => t == null) || !read.Any(t => t.OfType<Num>().Any())) return null;
        // Excel's rule: one section serves every value (a minus is added); two split positive and zero from negative; three add zero.
        var index = d.Negative && sections.Count >= 2 ? 1 : d.Zero && sections.Count >= 3 ? 2 : 0;
        var tokens = read[index];
        if (tokens.Count == 0) return index == 0 ? null : "";
        var value = d.Abs.Shift(2 * tokens.Count(t => t is Pct));
        var num = tokens.OfType<Num>().FirstOrDefault();
        string number = null;
        if (num != null && (number = Digits(value, num.Run, extra)) == null) return null;
        var text = string.Concat(tokens.Select(t => t switch { Num => number, Pct => "%", Lit l => l.Text, _ => "" }));
        return (d.Negative && sections.Count == 1 && !d.Zero ? "-" : "") + text;
    }

    /// <summary>The digits of one run of placeholders, such as "#,0.00", "0.0##", "#,0," (thousands) or "0.00E+00".</summary>
    private static string Digits(Dec value, string run, int extra)
    {
        var match = Run.Match(run);
        if (!match.Success || !match.Groups["int"].Value.Concat(match.Groups["dec"].Value).Any(c => c is '#' or '0')) return null;
        var whole = match.Groups["int"].Value;
        var decimals = match.Groups["dec"].Value;
        var keep = decimals.LastIndexOf('0') + 1;
        var fraction = Math.Max(decimals.Length, extra);
        if (match.Groups["e"].Success)
        {
            var places = Math.Max(1, whole.Count(c => c is '#' or '0'));
            var (mantissa, exponent) = Mantissa(value, places, fraction);
            var power = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).PadLeft(match.Groups["exp"].Value.Count(c => c == '0'), '0');
            var sign = exponent < 0 ? "-" : match.Groups["e"].Value[1] == '+' ? "+" : "";
            return Trimmed(mantissa.Int.PadLeft(whole.Count(c => c == '0'), '0'), mantissa.Frac, keep) + match.Groups["e"].Value[0] + sign + power;
        }
        // Each comma that ends the integer placeholders divides by a thousand ("#,0," shows 1234567 as 1,235).
        var thousands = whole.Length - whole.TrimEnd(',').Length;
        var (integer, frac) = Round(value.Shift(-3 * thousands), fraction);
        var zeros = Math.Max(1, whole.Count(c => c == '0'));
        // A comma between the placeholders groups the digits; with none ("0.00", "Fixed", "Percent") Power BI shows them ungrouped.
        var digits = integer.PadLeft(zeros, '0');
        return Trimmed(whole.TrimEnd(',').Contains(',') ? Group(digits) : digits, frac, keep);
    }

    /// <summary>The sections of a format, split on a ";" that is neither quoted nor escaped. Null when a quote or escape never closes.</summary>
    private static List<string> SectionsOf(string format)
    {
        var sections = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '"')
            {
                var end = format.IndexOf('"', i + 1);
                if (end < 0) return null;
                current.Append(format, i, end + 1 - i);
                i = end;
            }
            else if (c is '\\' or '_' or '*')
            {
                if (i + 1 >= format.Length) return null;
                current.Append(c).Append(format[++i]);
            }
            else if (c == ';') { sections.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        sections.Add(current.ToString());
        return sections.Count > 3 ? null : sections;
    }

    /// <summary>One section as literal text, "%" operators and at most ONE run of digit placeholders. Null for anything else.</summary>
    private static List<Tok> TokensOf(string section)
    {
        var tokens = new List<Tok>();
        var seenNumber = false;
        for (var i = 0; i < section.Length; i++)
        {
            var c = section[i];
            if (c == '"')
            {
                var end = section.IndexOf('"', i + 1);
                tokens.Add(new Lit(section.Substring(i + 1, end - i - 1)));
                i = end;
            }
            else if (c == '\\') tokens.Add(new Lit(section[++i].ToString()));
            else if (c == '_') { i++; tokens.Add(new Lit(" ")); } // the width of the next character: a space
            else if (c == '*') i++; // fill the cell with the next character: nothing in a sentence
            else if (c == '[')
            {
                // Excel's locale and currency code, [$-en-US] or [$€-407]: it names a locale and shows at most the currency sign.
                // A colour, [Red], changes no text. A condition, [>100], is not read here.
                var end = section.IndexOf(']', i + 1);
                if (end < 0) return null;
                var inner = section.Substring(i + 1, end - i - 1);
                var code = Locale.Match(inner);
                if (code.Success) { if (code.Groups["sign"].Value.Length > 0) tokens.Add(new Lit(code.Groups["sign"].Value)); }
                else if (!Colour.IsMatch(inner)) return null;
                i = end;
            }
            else if (c == '%') tokens.Add(new Pct());
            else if ("#0,.".Contains(c))
            {
                if (seenNumber && tokens[^1] is not Num) return null; // two runs of digits, such as 0.0 "x" 0.0
                var start = i;
                while (i + 1 < section.Length && "#0,.".Contains(section[i + 1])) i++;
                // An exponent belongs to the run: E+ or E- followed by digit placeholders.
                if (i + 2 < section.Length && section[i + 1] is 'E' or 'e' && section[i + 2] is '+' or '-')
                {
                    i += 2;
                    while (i + 1 < section.Length && section[i + 1] is '#' or '0') i++;
                }
                var text = section.Substring(start, i - start + 1);
                if (seenNumber) tokens[^1] = new Num(((Num)tokens[^1]).Run + text);
                else { seenNumber = true; tokens.Add(new Num(text)); }
            }
            else if (SafeLiteral.Contains(c)) tokens.Add(new Lit(c.ToString()));
            else return null; // a letter, an exponent without a run, a padding mark this reader does not know: not read here
        }
        return tokens;
    }
}
