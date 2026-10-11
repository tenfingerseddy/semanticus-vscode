// Pattern tests over a column's values, run INSIDE the engine.
//
// This is the one place raw values are looked at, and they go no further: every method here returns counts,
// shares or flags, never a value. The values come from a bounded read of the model's own data (FactsMeasurer)
// and are dropped as soon as the column has been classified.
//
// Shares are taken over DISTINCT non-blank values, so a value repeated on a million rows counts once. The flags
// (looks like a year, a month name, ...) require EVERY distinct non-blank value to fit, and at least one to exist,
// so one stray value is enough to say no. That is deliberate: a flag a family acts on should be a sure thing, and
// the family still sees the share facts when it needs a softer read.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Facts
{
    internal static class Gazetteer
    {
        internal const string ResourceName = "Semanticus.Engine.Facts.gazetteer.json";
        private static readonly Lazy<(HashSet<string> countries, HashSet<string> regions)> Lists = new Lazy<(HashSet<string>, HashSet<string>)>(Load);

        internal static HashSet<string> Countries => Lists.Value.countries;
        internal static HashSet<string> Regions => Lists.Value.regions;

        private static (HashSet<string>, HashSet<string>) Load()
        {
            // Fail loud, like the other embedded catalogs: a missing gazetteer is a build defect, not a model fact.
            using var stream = typeof(Gazetteer).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("The bundled place list is missing from this build (" + ResourceName + ").");
            using var doc = JsonDocument.Parse(stream);
            HashSet<string> Read(string name) => new HashSet<string>(
                doc.RootElement.GetProperty(name).EnumerateArray().Select(e => Normalize(e.GetString())), StringComparer.Ordinal);
            return (Read("countries"), Read("regions"));
        }

        internal static string Normalize(string s) => (s ?? "").Trim().ToUpperInvariant();
    }

    /// <summary>The value-derived part of <see cref="MeasuredFacts"/> for one column.</summary>
    internal sealed class ValueFactsResult
    {
        public long Distinct;
        public long Blanks;
        public long Rows;
        /// <summary>Every non-blank value is a number a double holds exactly (a whole number no further than
        /// 2^53 from zero, or a value that already is a double). A flag, never the values.</summary>
        public bool ExactInDouble;
        /// <summary>Every non-blank value fits a fixed decimal (Tabular's Decimal: four places, no further than
        /// 922,337,203,685,477.5807 from zero). A flag, never the values.</summary>
        public bool FitsFixedDecimal;
        /// <summary>Every non-blank value is a date. Only then does <see cref="ContiguousDays"/> mean anything.</summary>
        public bool AllDates;
        /// <summary>Every non-blank value is a whole day (no time part) and the distinct days run from the first to the
        /// last with no day missing: what a date table's date column must be. A flag, never the first or last day.</summary>
        public bool ContiguousDays;
        public bool WholeNumbers, LooksLikeYear, LooksLikeMonthName, LooksLikeMonthNumber, LooksLikeDayName, LooksLikeQuarter;
        /// <summary>Every value is a whole number from 1 to 53 and there are 2 to 53 of them: a day, week, month or
        /// period number. The fair columns replay's looksLikePeriodNumber (gather-fair.py period_number), word for word.</summary>
        public bool LooksLikePeriodNumber;
        public double Country, Region, Postcode, Url, ImageUrl, Email;
        /// <summary>Distinct keys in row order, used only for the one-to-one test and then dropped.</summary>
        public string[] RowKeys;
    }

    internal static class ValueFacts
    {
        private static readonly HashSet<string> MonthNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE", "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER",
            "JAN", "FEB", "MAR", "APR", "JUN", "JUL", "AUG", "SEP", "SEPT", "OCT", "NOV", "DEC",
        };
        private static readonly HashSet<string> DayNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "MONDAY", "TUESDAY", "WEDNESDAY", "THURSDAY", "FRIDAY", "SATURDAY", "SUNDAY",
            "MON", "TUE", "TUES", "WED", "THU", "THUR", "THURS", "FRI", "SAT", "SUN",
        };

        private const RegexOptions Opt = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        private static readonly Regex Quarter = new Regex(@"^(?:(?:q|qtr|quarter)\s*[1-4]|\d{4}\s*[-/ ]?\s*q[1-4]|q[1-4]\s*[-/ ]?\s*\d{4})$", Opt);
        private static readonly Regex FourDigits = new Regex(@"^\d{4}$", RegexOptions.CultureInvariant);
        private static readonly Regex Url = new Regex(@"^(?:https?://|www\.)\S+$", Opt);
        private static readonly Regex ImagePath = new Regex(@"\.(?:png|jpe?g|gif|svg|webp|bmp|ico|tiff?)$", Opt);
        private static readonly Regex Email = new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", Opt);
        // Postcodes: AU and NZ (four digits), US (ZIP or ZIP+4), UK (outward and inward code), CA (A1A 1A1).
        private static readonly Regex[] Postcodes =
        {
            new Regex(@"^\d{4}$", RegexOptions.CultureInvariant),
            new Regex(@"^\d{5}(?:-\d{4})?$", RegexOptions.CultureInvariant),
            new Regex(@"^(?:GIR ?0AA|[A-Z]{1,2}\d[A-Z\d]? ?\d[A-Z]{2})$", Opt),
            new Regex(@"^[ABCEGHJ-NPRSTVXY]\d[ABCEGHJ-NPRSTV-Z] ?\d[ABCEGHJ-NPRSTV-Z]\d$", Opt),
        };

        internal static bool IsBlank(object v) => v == null || v is DBNull || (v is string s && s.Length == 0);

        private static bool TryNumber(object v, out double d)
        {
            switch (v)
            {
                case long l: d = l; return true;
                case int i: d = i; return true;
                case short sh: d = sh; return true;
                case byte b: d = b; return true;
                case double db: d = db; return !double.IsNaN(db) && !double.IsInfinity(db);
                case float f: d = f; return !float.IsNaN(f) && !float.IsInfinity(f);
                case decimal m: d = (double)m; return true;
                default: d = 0; return false;
            }
        }

        private static bool IsWhole(double d) => Math.Abs(d - Math.Round(d)) < 1e-9;

        /// <summary>2^53: every whole number no further than this from zero has an exact double, and
        /// 2^53 + 1 is the first that does not.</summary>
        internal const long ExactDoubleLimit = 9007199254740992L;

        /// <summary>Whether a double holds this value exactly. Decided on the value's own type, never by
        /// converting it first, because the conversion is the thing that loses it.</summary>
        /// <summary>The largest whole number a fixed decimal holds: its range is +/- 922,337,203,685,477.5807.</summary>
        internal const long FixedDecimalWholeLimit = 922337203685477L;
        private const decimal FixedDecimalLimit = 922337203685477.5807m;

        /// <summary>Whether a fixed decimal holds this value exactly: in range and no more than four places.
        /// Decided on the value's own type, never by converting it first.</summary>
        internal static bool FitsFixedDecimalValue(object v) => v switch
        {
            long l => l >= -FixedDecimalWholeLimit && l <= FixedDecimalWholeLimit,
            int or short or byte => true,
            decimal m => m >= -FixedDecimalLimit && m <= FixedDecimalLimit && m == decimal.Round(m, 4),
            double db => !double.IsNaN(db) && !double.IsInfinity(db) && Math.Abs(db) <= FixedDecimalWholeLimit
                         && (decimal)db == decimal.Round((decimal)db, 4),
            _ => false,
        };

        internal static bool IsExactInDouble(object v) => v switch
        {
            long l => l >= -ExactDoubleLimit && l <= ExactDoubleLimit,
            int or short or byte => true,
            double db => !double.IsNaN(db) && !double.IsInfinity(db),
            float f => !float.IsNaN(f) && !float.IsInfinity(f),
            decimal m => m == decimal.Truncate(m) && m >= -ExactDoubleLimit && m <= ExactDoubleLimit,
            _ => false,
        };

        /// <summary>A stable key per value: type-tagged so the number 1 and the text "1" stay distinct values.
        /// An integer is keyed by its exact value, never through a double: a double cannot tell
        /// 9007199254740992 from 9007199254740993, and keying through one made them one value, so distinct counts
        /// and uniqueness came out wrong for large keys. The integer types share one spelling, so 7 is one value
        /// whichever of them carries it.</summary>
        internal static string Key(object v)
        {
            if (IsBlank(v)) return "_";   // every other key carries a one-letter type tag, so "_" cannot collide
            return v switch
            {
                string s => "s" + s,
                DateTime dt => "t" + dt.Ticks.ToString(CultureInfo.InvariantCulture),
                bool b => b ? "b1" : "b0",
                long l => "n" + l.ToString(CultureInfo.InvariantCulture),
                int i => "n" + i.ToString(CultureInfo.InvariantCulture),
                short sh => "n" + sh.ToString(CultureInfo.InvariantCulture),
                byte by => "n" + by.ToString(CultureInfo.InvariantCulture),
                // A decimal by its exact value too: through a double, two decimals that differ past the fifteenth
                // digit became one. Trailing zeros are dropped first (1.50 is 1.5, 7.0000 is 7), so the same number
                // reads as one value whatever scale or integer type carries it.
                decimal m => "n" + (m / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
                _ => TryNumber(v, out var d) ? "n" + d.ToString("R", CultureInfo.InvariantCulture) : "o" + Convert.ToString(v, CultureInfo.InvariantCulture),
            };
        }

        internal static ValueFactsResult Compute(IReadOnlyList<object> values)
        {
            var r = new ValueFactsResult { Rows = values.Count, RowKeys = new string[values.Count] };
            var distinct = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i < values.Count; i++)
            {
                var v = values[i];
                var k = Key(v);
                r.RowKeys[i] = k;
                if (IsBlank(v)) r.Blanks++;
                if (!distinct.ContainsKey(k)) distinct[k] = IsBlank(v) ? null : v;
            }
            r.Distinct = distinct.Count;

            var nonBlank = distinct.Values.Where(v => v != null).ToList();
            if (nonBlank.Count == 0) return r;

            bool all(Func<object, bool> test) => nonBlank.All(test);
            double shareOfText(Func<string, bool> test)
            {
                var hits = nonBlank.Count(v => v is string s && test(s.Trim()));
                return Math.Round((double)hits / nonBlank.Count, 4);
            }

            r.AllDates = all(v => v is DateTime);
            if (r.AllDates) r.ContiguousDays = ContiguousDays(nonBlank.Cast<DateTime>().ToList());
            r.WholeNumbers = all(v => TryNumber(v, out var d) && IsWhole(d));
            r.ExactInDouble = all(IsExactInDouble);
            r.FitsFixedDecimal = all(FitsFixedDecimalValue);
            r.LooksLikeYear = all(v =>
                (TryNumber(v, out var d) && IsWhole(d) && d >= 1900 && d <= 2100)
                || (v is string s && FourDigits.IsMatch(s.Trim()) && int.TryParse(s.Trim(), out var y) && y >= 1900 && y <= 2100));
            r.LooksLikeMonthNumber = all(v => TryNumber(v, out var d) && IsWhole(d) && d >= 1 && d <= 12);
            r.LooksLikeMonthName = all(v => v is string s && MonthNames.Contains(Gazetteer.Normalize(s)));
            r.LooksLikeDayName = all(v => v is string s && DayNames.Contains(Gazetteer.Normalize(s)));
            r.LooksLikeQuarter = all(v => (v is string s && Quarter.IsMatch(s.Trim())) || (TryNumber(v, out var d) && IsWhole(d) && d >= 1 && d <= 4));
            r.LooksLikePeriodNumber = nonBlank.Count >= 2 && nonBlank.Count <= 53
                                      && all(v => !(v is string) && TryNumber(v, out var d) && IsWhole(d) && d >= 1 && d <= 53);

            r.Country = shareOfText(s => Gazetteer.Countries.Contains(Gazetteer.Normalize(s)));
            r.Region = shareOfText(s => Gazetteer.Regions.Contains(Gazetteer.Normalize(s)));
            r.Postcode = shareOfText(s => Postcodes.Any(p => p.IsMatch(s)));
            r.Url = shareOfText(s => Url.IsMatch(s));
            r.ImageUrl = shareOfText(s => Url.IsMatch(s) && ImagePath.IsMatch(s.Split('?', '#')[0]));
            r.Email = shareOfText(s => Email.IsMatch(s));
            return r;
        }

        /// <summary>Distinct whole days with none missing between the first and the last. The span is worked out and
        /// dropped here; only the yes or no leaves.</summary>
        internal static bool ContiguousDays(IReadOnlyList<DateTime> distinct)
        {
            if (distinct.Count == 0 || distinct.Any(d => d.TimeOfDay != TimeSpan.Zero)) return false;
            var first = distinct.Min();
            var last = distinct.Max();
            return (long)(last - first).TotalDays + 1 == distinct.Count;
        }

        /// <summary>distinct(pair) == distinct(each): every value of one column pairs with exactly one of the other.</summary>
        internal static bool OneToOne(ValueFactsResult a, ValueFactsResult b)
        {
            if (a.Distinct != b.Distinct || a.RowKeys.Length != b.RowKeys.Length) return false;
            var pairs = new HashSet<(string, string)>();
            for (var i = 0; i < a.RowKeys.Length; i++)
            {
                pairs.Add((a.RowKeys[i], b.RowKeys[i]));
                if (pairs.Count > a.Distinct) return false;
            }
            return pairs.Count == a.Distinct;
        }
    }
}
