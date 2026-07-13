using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    /// <summary>The time-intelligence identities that E6 can verify without a second source of truth.</summary>
    // Both converters: the RPC pipe (Newtonsoft) and the MCP door (System.Text.Json) each need the
    // string form — without these the wire carries ints and the UI's kind-keyed labels silently miss.
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public enum TiVariantKind { Ytd, Qtd, Mtd, PriorYear, YearOverYearDelta, Unrecognized }

    /// <summary>A conservative classification of one variant expression. Unrecognized is an honest result:
    /// an unfamiliar expression is never guessed into a test that could mint a false Pass.</summary>
    public sealed class TiClassification
    {
        public TiVariantKind Kind;
        public string DateColumnRef;
        public string BaseMeasure;
        public string Reason;
    }

    /// <summary>One pure DAX identity probe. A live adapter may execute it and return its two scalar values.</summary>
    public sealed class TiIdentityQuery
    {
        public string Dax;
        public string Description;
    }

    /// <summary>The outcome of comparing a TI variant with the deterministic value implied by its base.</summary>
    public sealed class TiVariantVerdict
    {
        public string Variant;
        public TiVariantKind Kind;
        public Verdict Verdict;
        public string Explanation;
    }

    /// <summary>A complete calendar period selected from a date column's observed range.</summary>
    public sealed class TiSamplePeriod
    {
        public DateTime PeriodStart;
        public DateTime PeriodEnd;
        public DateTime PriorStart;
        public DateTime PriorEnd;
        public string Reason;
        public bool Verifiable => string.IsNullOrEmpty(Reason);
    }

    /// <summary>
    /// E6 time-intelligence variant verification. This is a PURE core: classification and query generation are
    /// deterministic text operations, and judgement consumes values already obtained by a caller. It never opens
    /// a model or connection. The classifier deliberately recognizes only a small set of explicit DAX shapes.
    /// </summary>
    public static class TimeIntelligenceVariants
    {
        private const decimal AbsoluteTolerance = 0.000000001m;
        private const decimal RelativeTolerance = 0.0000001m;

        private static readonly Regex TiFunction = new Regex(
            @"\b(TOTALYTD|DATESYTD|TOTALQTD|DATESQTD|TOTALMTD|DATESMTD|SAMEPERIODLASTYEAR|DATEADD)\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex DateColumn = new Regex(
            @"'(?<table>(?:''|[^'])+)'\[(?<column>(?:\]\]|[^\]])+)\]",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Classify only a recognized, unambiguous TI expression tied to the expected base measure.</summary>
        public static TiClassification Classify(string variantDax, string expectedBaseMeasure)
        {
            var result = new TiClassification { Kind = TiVariantKind.Unrecognized, BaseMeasure = expectedBaseMeasure };
            var stripped = StripComments(variantDax);
            var scan = MaskStringLiterals(stripped);
            var calls = FindTiCalls(scan);

            if (calls.Count == 0)
                return Unrecognized(result, "pattern not recognized");

            // Two distinct TI functions leave the intended identity unclear even when they happen to belong to
            // the same family. Refusing the expression is safer than choosing whichever token appeared first.
            if (calls.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                return Unrecognized(result, "multiple different TI functions are present");

            if (string.IsNullOrWhiteSpace(expectedBaseMeasure) || !ContainsMeasureReference(scan, expectedBaseMeasure))
                return Unrecognized(result, "expected base measure reference is missing");

            var dateRefs = calls
                .SelectMany(c => ExtractDateColumns(c.Arguments))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (dateRefs.Count == 0)
                return Unrecognized(result, "date column reference could not be extracted");
            if (dateRefs.Count > 1)
                return Unrecognized(result, "multiple date column references are present");

            var kind = calls[0].Kind;
            if (kind == TiVariantKind.PriorYear && IsYearOverYearExpression(scan, calls, expectedBaseMeasure))
                kind = TiVariantKind.YearOverYearDelta;

            result.Kind = kind;
            result.DateColumnRef = dateRefs[0];
            result.Reason = null;
            return result;
        }

        /// <summary>Select the latest complete calendar period supported by the observed date range.</summary>
        public static TiSamplePeriod SelectSamplePeriod(DateTime min, DateTime max, TiVariantKind kind)
        {
            min = min.Date;
            max = max.Date;
            if (min > max || kind == TiVariantKind.Unrecognized)
                return NoPeriod("no complete period in the data");

            DateTime start;
            DateTime end;
            switch (kind)
            {
                case TiVariantKind.Mtd:
                    var month = new DateTime(max.Year, max.Month, 1);
                    end = new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
                    if (end > max)
                    {
                        if (month.Year == 1 && month.Month == 1) return NoPeriod("no complete period in the data");
                        month = month.Month == 1 ? new DateTime(month.Year - 1, 12, 1) : new DateTime(month.Year, month.Month - 1, 1);
                        end = new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
                    }
                    start = month;
                    break;
                case TiVariantKind.Qtd:
                    var quarterMonth = ((max.Month - 1) / 3) * 3 + 1;
                    start = new DateTime(max.Year, quarterMonth, 1);
                    var quarterEndMonth = quarterMonth + 2;
                    end = new DateTime(start.Year, quarterEndMonth, DateTime.DaysInMonth(start.Year, quarterEndMonth));
                    if (end > max)
                    {
                        if (start.Year == 1 && quarterMonth == 1) return NoPeriod("no complete period in the data");
                        start = quarterMonth == 1 ? new DateTime(start.Year - 1, 10, 1) : new DateTime(start.Year, quarterMonth - 3, 1);
                        quarterEndMonth = start.Month + 2;
                        end = new DateTime(start.Year, quarterEndMonth, DateTime.DaysInMonth(start.Year, quarterEndMonth));
                    }
                    break;
                default:
                    start = new DateTime(max.Year, 1, 1);
                    end = new DateTime(max.Year, 12, 31);
                    if (end > max)
                    {
                        if (start.Year == 1) return NoPeriod("no complete period in the data");
                        start = new DateTime(start.Year - 1, 1, 1);
                        end = new DateTime(start.Year, 12, 31);
                    }
                    break;
            }

            if (start < min || end > max)
                return NoPeriod("no complete period in the data");

            if (kind == TiVariantKind.PriorYear || kind == TiVariantKind.YearOverYearDelta)
            {
                if (start.Year == 1)
                    return NoPeriod("the data does not span two complete years");
                var priorStart = start.AddYears(-1);
                var priorEnd = end.AddYears(-1);
                if (priorStart < min || priorEnd > max)
                    return NoPeriod("the data does not span two complete years");
                return new TiSamplePeriod
                {
                    PeriodStart = start, PeriodEnd = end,
                    PriorStart = priorStart, PriorEnd = priorEnd,
                };
            }

            return new TiSamplePeriod
            {
                PeriodStart = start, PeriodEnd = end,
                PriorStart = start, PriorEnd = end,
            };
        }

        /// <summary>
        /// Strip DAX line and block comments while preserving quoted strings and table identifiers. Newlines are
        /// retained so tokens on separate lines cannot be accidentally joined into a different expression.
        /// </summary>
        public static string StripComments(string dax)
        {
            if (string.IsNullOrEmpty(dax)) return string.Empty;
            var output = new StringBuilder(dax.Length);
            var inString = false;
            var inTableName = false;

            for (var i = 0; i < dax.Length; i++)
            {
                var ch = dax[i];
                var next = i + 1 < dax.Length ? dax[i + 1] : '\0';

                if (inString)
                {
                    output.Append(ch);
                    if (ch == '"')
                    {
                        if (next == '"') output.Append(dax[++i]);
                        else inString = false;
                    }
                    continue;
                }
                if (inTableName)
                {
                    output.Append(ch);
                    if (ch == '\'')
                    {
                        if (next == '\'') output.Append(dax[++i]);
                        else inTableName = false;
                    }
                    continue;
                }
                if (ch == '"') { inString = true; output.Append(ch); continue; }
                if (ch == '\'') { inTableName = true; output.Append(ch); continue; }

                if ((ch == '/' && next == '/') || (ch == '-' && next == '-'))
                {
                    i += 2;
                    while (i < dax.Length && dax[i] != '\r' && dax[i] != '\n') i++;
                    if (i < dax.Length) output.Append(dax[i]);
                    continue;
                }
                if (ch == '/' && next == '*')
                {
                    i += 2;
                    while (i < dax.Length && !(dax[i] == '*' && i + 1 < dax.Length && dax[i + 1] == '/'))
                    {
                        if (dax[i] == '\r' || dax[i] == '\n') output.Append(dax[i]);
                        i++;
                    }
                    if (i < dax.Length) i++;
                    continue;
                }
                output.Append(ch);
            }
            return output.ToString();
        }

        /// <summary>Build a one-row, two-column DAX query for the classified identity.</summary>
        public static TiIdentityQuery BuildIdentityQuery(
            TiClassification c,
            string variantMeasure,
            DateTime periodStart,
            DateTime periodEnd,
            DateTime priorStart,
            DateTime priorEnd)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            if (c.Kind == TiVariantKind.Unrecognized)
                throw new ArgumentException("an unrecognized time-intelligence pattern has no safe identity query", nameof(c));
            if (string.IsNullOrWhiteSpace(variantMeasure))
                throw new ArgumentException("variant measure must be named", nameof(variantMeasure));
            if (string.IsNullOrWhiteSpace(c.BaseMeasure))
                throw new ArgumentException("classification must name its base measure", nameof(c));

            var date = NormalizeDateColumn(c.DateColumnRef);
            var variant = MeasureRef(variantMeasure);
            var baseMeasure = MeasureRef(c.BaseMeasure);
            var period = DatesBetween(date, periodStart, periodEnd);
            var prior = DatesBetween(date, priorStart, priorEnd);

            string variantExpression;
            string expectedExpression;
            string description;
            switch (c.Kind)
            {
                case TiVariantKind.Ytd:
                case TiVariantKind.Qtd:
                case TiVariantKind.Mtd:
                    variantExpression = $"CALCULATE({variant}, {DatesBetween(date, periodEnd, periodEnd)})";
                    expectedExpression = $"CALCULATE({baseMeasure}, {period})";
                    description = $"{c.Kind} at the period end compared with the base measure over the complete period";
                    break;
                case TiVariantKind.PriorYear:
                    variantExpression = $"CALCULATE({variant}, {period})";
                    expectedExpression = $"CALCULATE({baseMeasure}, {prior})";
                    description = "Prior-year variant over the sampled period compared with the base measure over the prior period";
                    break;
                case TiVariantKind.YearOverYearDelta:
                    variantExpression = $"CALCULATE({variant}, {period})";
                    expectedExpression = $"CALCULATE({baseMeasure}, {period}) - CALCULATE({baseMeasure}, {prior})";
                    description = "Year-over-year delta compared with the difference between current and prior base values";
                    break;
                default:
                    throw new ArgumentException("the classification kind has no time-intelligence identity", nameof(c));
            }

            return new TiIdentityQuery
            {
                Dax = $@"EVALUATE
ROW(
    ""__ti_variant"", {variantExpression},
    ""__ti_expected"", {expectedExpression}
)",
                Description = description,
            };
        }

        /// <summary>Judge already-executed scalar results using the same mixed absolute/relative tolerance style.</summary>
        public static TiVariantVerdict Judge(
            TiClassification c,
            string variantMeasure,
            decimal? variantValue,
            decimal? expectedValue,
            bool executed)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            var verdict = new TiVariantVerdict { Variant = variantMeasure, Kind = c.Kind };

            if (c.Kind == TiVariantKind.Unrecognized)
                return Set(verdict, Verdict.NotVerifiable, "Not verifiable: " + (string.IsNullOrWhiteSpace(c.Reason) ? "pattern not recognized" : c.Reason) + ".");
            if (!executed)
                return Set(verdict, Verdict.NotVerifiable, "Not verifiable: the identity query was not run.");
            if (variantValue == null && expectedValue == null)
                return Set(verdict, Verdict.NotVerifiable, "Not verifiable: both sides blank over the sampled period.");
            if (variantValue == null)
                return Set(verdict, Verdict.NotVerifiable, "Not verifiable: the variant side is blank over the sampled period.");
            if (expectedValue == null)
                return Set(verdict, Verdict.NotVerifiable, "Not verifiable: the expected side is blank over the sampled period.");

            var delta = Math.Abs(variantValue.Value - expectedValue.Value);
            var tolerance = Math.Max(AbsoluteTolerance, RelativeTolerance * Math.Abs(expectedValue.Value));
            var variantText = Number(variantValue.Value);
            var expectedText = Number(expectedValue.Value);
            var deltaText = Number(delta);
            if (delta <= tolerance)
                return Set(verdict, Verdict.Pass, $"Variant value {variantText} matches expected value {expectedText}, delta: {deltaText}.");
            return Set(verdict, Verdict.Fail, $"Variant value {variantText} does not match expected value {expectedText}, delta: {deltaText}.");
        }

        private static TiClassification Unrecognized(TiClassification result, string reason)
        {
            result.Kind = TiVariantKind.Unrecognized;
            result.DateColumnRef = null;
            result.Reason = reason;
            return result;
        }

        private static TiSamplePeriod NoPeriod(string reason) => new TiSamplePeriod { Reason = reason };

        private static TiVariantVerdict Set(TiVariantVerdict result, Verdict verdict, string explanation)
        {
            result.Verdict = verdict;
            result.Explanation = explanation;
            return result;
        }

        private static string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

        private static string MeasureRef(string measure) => "[" + measure.Replace("]", "]]" ) + "]";

        private static bool ContainsMeasureReference(string dax, string measure)
        {
            var pattern = @"(?<!['A-Za-z0-9_])" + Regex.Escape(MeasureRef(measure));
            return Regex.IsMatch(dax, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string NormalizeDateColumn(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("classification must contain an extractable date column reference", nameof(reference));
            var match = DateColumn.Match(reference.Trim());
            if (!match.Success || match.Index != 0 || match.Length != reference.Trim().Length)
                throw new ArgumentException("date column reference must use the form 'Table'[Column]", nameof(reference));
            var table = match.Groups["table"].Value.Replace("''", "'");
            var column = match.Groups["column"].Value.Replace("]]", "]");
            return QuoteTable(table) + "[" + column.Replace("]", "]]") + "]";
        }

        private static string QuoteTable(string table) => "'" + table.Replace("'", "''") + "'";

        private static string DatesBetween(string date, DateTime start, DateTime end)
            => $"DATESBETWEEN({date}, {Date(start)}, {Date(end)})";

        private static string Date(DateTime value) => $"DATE({value.Year},{value.Month},{value.Day})";

        private static List<TiCall> FindTiCalls(string dax)
        {
            var calls = new List<TiCall>();
            foreach (Match match in TiFunction.Matches(dax))
            {
                var name = match.Groups[1].Value;
                var open = match.Index + match.Length - 1;
                var close = FindClosingParenthesis(dax, open);
                if (close < 0) continue;
                var arguments = dax.Substring(open + 1, close - open - 1);
                var kind = KindFor(name, arguments);
                if (kind != TiVariantKind.Unrecognized)
                    calls.Add(new TiCall { Name = name, Kind = kind, Start = match.Index, End = close, Arguments = arguments });
            }
            return calls;
        }

        private static TiVariantKind KindFor(string name, string arguments)
        {
            if (name.Equals("TOTALYTD", StringComparison.OrdinalIgnoreCase) || name.Equals("DATESYTD", StringComparison.OrdinalIgnoreCase)) return TiVariantKind.Ytd;
            if (name.Equals("TOTALQTD", StringComparison.OrdinalIgnoreCase) || name.Equals("DATESQTD", StringComparison.OrdinalIgnoreCase)) return TiVariantKind.Qtd;
            if (name.Equals("TOTALMTD", StringComparison.OrdinalIgnoreCase) || name.Equals("DATESMTD", StringComparison.OrdinalIgnoreCase)) return TiVariantKind.Mtd;
            if (name.Equals("SAMEPERIODLASTYEAR", StringComparison.OrdinalIgnoreCase)) return TiVariantKind.PriorYear;
            if (name.Equals("DATEADD", StringComparison.OrdinalIgnoreCase))
            {
                var parts = SplitTopLevelArguments(arguments);
                if (parts.Count == 3 && Regex.IsMatch(parts[1], @"^\s*-\s*1\s*$") && Regex.IsMatch(parts[2], @"^\s*YEAR\s*$", RegexOptions.IgnoreCase))
                    return TiVariantKind.PriorYear;
            }
            return TiVariantKind.Unrecognized;
        }

        private static IEnumerable<string> ExtractDateColumns(string arguments)
        {
            foreach (Match match in DateColumn.Matches(arguments))
            {
                var table = match.Groups["table"].Value.Replace("''", "'");
                var column = match.Groups["column"].Value.Replace("]]", "]");
                yield return QuoteTable(table) + "[" + column.Replace("]", "]]") + "]";
            }
        }

        private static bool IsYearOverYearExpression(string dax, IReadOnlyList<TiCall> tiCalls, string baseMeasure)
        {
            var measurePattern = @"(?<!['A-Za-z0-9_])" + Regex.Escape(MeasureRef(baseMeasure));
            foreach (Match calculate in Regex.Matches(dax, @"\bCALCULATE\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var open = calculate.Index + calculate.Length - 1;
                var close = FindClosingParenthesis(dax, open);
                if (close < 0) continue;
                if (!tiCalls.Any(t => t.Start > open && t.End < close)) continue;
                var inside = dax.Substring(open + 1, close - open - 1);
                if (!Regex.IsMatch(inside, measurePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;

                var before = dax.Substring(0, calculate.Index);
                if (Regex.IsMatch(before, measurePattern + @"\s*[-/]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
                if (Regex.IsMatch(before, @"\bDIVIDE\s*\(\s*" + measurePattern + @"\s*,\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return true;
            }
            return false;
        }

        private static int FindClosingParenthesis(string text, int open)
        {
            var depth = 0;
            var inString = false;
            var inTableName = false;
            var inBracket = false;
            for (var i = open; i < text.Length; i++)
            {
                var ch = text[i];
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (inString)
                {
                    if (ch == '"') { if (next == '"') i++; else inString = false; }
                    continue;
                }
                if (inTableName)
                {
                    if (ch == '\'') { if (next == '\'') i++; else inTableName = false; }
                    continue;
                }
                if (inBracket)
                {
                    if (ch == ']') { if (next == ']') i++; else inBracket = false; }
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '\'') { inTableName = true; continue; }
                if (ch == '[') { inBracket = true; continue; }
                if (ch == '(') depth++;
                else if (ch == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static List<string> SplitTopLevelArguments(string text)
        {
            var parts = new List<string>();
            var start = 0;
            var depth = 0;
            var inString = false;
            var inTableName = false;
            var inBracket = false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                var next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (inString) { if (ch == '"') { if (next == '"') i++; else inString = false; } continue; }
                if (inTableName) { if (ch == '\'') { if (next == '\'') i++; else inTableName = false; } continue; }
                if (inBracket) { if (ch == ']') { if (next == ']') i++; else inBracket = false; } continue; }
                if (ch == '"') { inString = true; continue; }
                if (ch == '\'') { inTableName = true; continue; }
                if (ch == '[') { inBracket = true; continue; }
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (ch == ',' && depth == 0) { parts.Add(text.Substring(start, i - start)); start = i + 1; }
            }
            parts.Add(text.Substring(start));
            return parts;
        }

        private static string MaskStringLiterals(string dax)
        {
            var chars = dax.ToCharArray();
            var inString = false;
            for (var i = 0; i < chars.Length; i++)
            {
                if (!inString)
                {
                    if (chars[i] == '"') inString = true;
                    continue;
                }
                if (chars[i] == '"')
                {
                    if (i + 1 < chars.Length && chars[i + 1] == '"') { chars[i + 1] = ' '; i++; }
                    else inString = false;
                }
                else if (chars[i] != '\r' && chars[i] != '\n') chars[i] = ' ';
            }
            return new string(chars);
        }

        private sealed class TiCall
        {
            public string Name;
            public TiVariantKind Kind;
            public int Start;
            public int End;
            public string Arguments;
        }
    }
}
