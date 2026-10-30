using System;
using System.Globalization;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// The Filters sentence and the Equivalence card must name a number the same way, and the way Power BI shows it: the value's
// shortest round-trip text, read as an exact decimal, then the format string. The integer digits are always grouped, as the
// cards have grouped them since fx19. Both surfaces read the text MarkNumberFormat returns.
public sealed class MarkNumberFormatTests
{
    [Theory]
    [InlineData(628370, "[$$-409]#,##0.00", "$628,370.00")]
    [InlineData(628370, "Currency", "$628,370.00")]
    [InlineData(628370, "Standard", "628,370.00")]
    [InlineData(628370, "", "628,370")]
    [InlineData(628370, null, "628,370")]
    [InlineData(628370, "#,0", "628,370")]
    [InlineData(0.125, "0.00%", "12.50%")]
    [InlineData(-1234.5, "$#,0.00;($#,0.00)", "($1,234.50)")]
    [InlineData(0.0068, "0.0%", "0.7%")]
    [InlineData(36111492.86, "\\$#,0.00", "$36,111,492.86")]
    [InlineData(-5, "0", "-5")]
    [InlineData(0, "0;-0;\"zero\"", "zero")]
    [InlineData(1234567.891, "#,##0.0", "1,234,567.9")]
    [InlineData(1234.5, "0.####", "1234.5")]
    [InlineData(12, "0.0\"%\"", "12.0%")]
    [InlineData(1234.5, "[$€-407]#,0.00", "€1,234.50")]
    [InlineData(-0.5, "0.0%;(0.0%)", "(50.0%)")]
    [InlineData(1e9, "#,0", "1,000,000,000")]
    [InlineData(999.5, "#,0", "1,000")]
    [InlineData(1234567, "#,0,", "1,235")]
    [InlineData(1234567890, "#,0,,\" M\"", "1,235 M")]
    [InlineData(5, "0.0 \"x\" 0.0", "5")]
    [InlineData(1234.56789, "General", "1,234.56789")]
    [InlineData(1234.56789, "C2", "1,234.56789")]
    [InlineData(1234.56789, "0.00E+00", "1.23E+03")]
    [InlineData(0.000123, "0.0E-0", "1.2E-4")]
    [InlineData(9.996, "0.00E+00", "1.00E+01")]
    [InlineData(100, "\"abc", "100")]
    [InlineData(0.5, "0%", "50%")]
    [InlineData(1500, "#,0;\"neg\"", "1,500")]
    [InlineData(-1500, "#,0;\"neg\"", "neg")]
    [InlineData(0, "#,0.00", "0.00")]
    [InlineData(2.4999, "0.00", "2.50")]
    [InlineData(-1234.5, "Currency", "($1,234.50)")]
    [InlineData(1234.5, "Fixed", "1234.50")]
    [InlineData(1234.5, "0.00", "1234.50")]
    [InlineData(1234.5, "#,0.00", "1,234.50")]
    [InlineData(1234.5, "0.00%", "123450.00%")]
    [InlineData(1234567, "0,", "1235")]
    [InlineData(0.5, "Yes/No", "Yes")]
    [InlineData(0, "On/Off", "Off")]
    [InlineData(-1234.5, "[Red]#,0.00;[Blue](#,0.00)", "(1,234.50)")]
    [InlineData(1234.5, "[>100]#,0", "1,234.5")]
    [InlineData(1234.5, "#,0.00_);(#,0.00)", "1,234.50 ")]
    public void Filters_and_Equivalence_name_a_number_the_same_way(double value, string format, string expected) =>
        Assert.Equal(expected, MarkNumberFormat.Display(value, format));

    // v-g1b: .NET custom formats round a double at 15 significant digits first; equivalenceNumber (Intl) rounds the shortest
    // round-trip value. A float sum just under a half, or a value with 16 or more digits, then shows two different numbers.
    // With no format the number shows in General: every integer digit, at most 15 significant digits in all.
    [Theory]
    [InlineData(1234.4999999999998, "#,0", "1,234")]
    [InlineData(628369.4999999999, "[$$-409]#,##0", "$628,369")]
    [InlineData(-1234.4999999999998, "$#,0;($#,0)", "($1,234)")]
    [InlineData(0.12344999999999999, "", "0.12345")]
    [InlineData(123456789012.3456, null, "123,456,789,012.346")]
    [InlineData(12345678901234.56, "#,0.00", "12,345,678,901,234.56")]
    [InlineData(1234567890123456, "#,0", "1,234,567,890,123,456")]
    public void Filters_and_Equivalence_round_a_number_the_same_way(double value, string format, string expected) =>
        Assert.Equal(expected, MarkNumberFormat.Display(value, format));

    // fx27: two rows with no grouping comma ("0.0##" and "0") expected grouped digits; Power BI does not group them, so they read
    // 1234.5 and 9007199254740992 now.
    // The 65 cases v-g1b ran against both formatters (cases.json and cases2.json). Each expected text is Power BI's rule worked by
    // hand: the shortest round-trip value, rounded half away from zero as a decimal, then the format string. Some literals are not
    // their own shortest text: 1.0049999999999999 is the double 1.005, and 1.2345678901234567e21 reads 1.2345678901234568E+21.
    public static readonly TheoryData<double, string, string> VG1bCases = new()
    {
        { 0.125, "0.00%", "12.50%" }, { 0.0068, "0.0%", "0.7%" }, { -0.0068, "0.0%", "-0.7%" }, { 0.12345, "0%", "12%" },
        { 1.5, "0.00%;-0.00%;0.00%", "150.00%" }, { -0.5, "0.0%;(0.0%)", "(50.0%)" }, { -1234.5, "#,0.00", "-1,234.50" },
        { -1234.5, "$#,0.00;($#,0.00)", "($1,234.50)" }, { -0.001, "0.00", "-0.00" }, { -0.00001, "", "-0.00001" },
        { -0.00001, null, "-0.00001" }, { -5, "0", "-5" }, { 2.675, "0.00", "2.68" }, { 1.005, "0.00", "1.01" },
        { 0.125, "0.00", "0.13" }, { 0.3, "0.000000", "0.300000" }, { 1234.56789, "#,0.###", "1,234.568" },
        { 1234.5, "0.0##", "1234.5" }, { 0.5, "#.##", "0.5" }, { 628370, "[$$-409]#,##0.00", "$628,370.00" },
        { 1234.5, "[$€-407]#,0.00", "€1,234.50" }, { 1234.5, "[$-en-US]#,0.00", "1,234.50" }, { 1234.5, "[$£-809]#,##0", "£1,235" },
        { -1234.5, "[$$-409]#,##0.00", "-$1,234.50" }, { 628370, "General Number", "628,370" },
        { 1234.56789, "General Number", "1,234.56789" }, { 0.123456, "Percent", "12.35%" }, { 628370, "Currency", "$628,370.00" },
        { 628370, "SELECTEDVALUE('Fmt'[Fmt], \"#,0\")", "628,370" }, { 628370, "IF([X] > 1, \"#,0.00\", \"0%\")", "628,370" },
        { 628370, "\"#,0.00\"", "628,370" }, { 1e15, "#,0", "1,000,000,000,000,000" }, { 1e16, "#,0", "10,000,000,000,000,000" },
        { 12345678901234567, "#,0", "12,345,678,901,234,568" }, { 123456789012345678, "", "123,456,789,012,345,680" },
        { 1.2345678901234567e20, "#,0.00", "123,456,789,012,345,670,000.00" }, { 9007199254740993, "0", "9007199254740992" },
        { 1e21, "", "1,000,000,000,000,000,000,000" }, { 1.2345678901234567e21, "#,0", "1,234,567,890,123,456,800,000" },
        { 1e-7, "", "0.0000001" }, { 1.23456789e-5, "0.00000000", "0.00001235" }, { 1e-10, "0.0000%", "0.0000%" },
        { 5e-324, "", "5E-324" }, { 0.00005, "", "0.00005" }, { 0.00015, "", "0.00015" }, { 0.000049999, "0.0000", "0.0000" },
        { 0.1, "0.0000000000000000", "0.1000000000000000" }, { 0.3333333333333333, "0.00000000000000000", "0.33333333333333330" },
        { 1234567.891, "#,##0.0", "1,234,567.9" }, { 0.1, "0.00000000000000000000", "0.10000000000000000000" },
        { 1234.4999999999998, "#,0", "1,234" }, { 0.49999999999999994, "0", "0" }, { 2.4999999999999996, "0", "2" },
        { 0.12344999999999999, "", "0.12345" }, { 0.12344999999999999, null, "0.12345" }, { 1.0049999999999999, "0.00", "1.01" },
        { 0.0049999999999999992, "0.0%", "0.5%" }, { 628369.49999999988, "[$$-409]#,##0", "$628,369" },
        { -1234.4999999999998, "$#,0;($#,0)", "($1,234)" }, { 1234567890123456, "#,0", "1,234,567,890,123,456" },
        { 12345678901234.56, "#,0.00", "12,345,678,901,234.56" }, { 123456789012.3456, "", "123,456,789,012.346" },
        { 12345678901.98765, "", "12,345,678,901.9877" }, { 628370.30000000005, "#,0.00", "628,370.30" },
        { 0.30000000000000004, "0.0000000000000000", "0.3000000000000000" },
    };

    [Fact]
    public void The_v_g1b_table_has_65_cases() => Assert.Equal(65, VG1bCases.Count());

    // Each row with its case number: two rows hold the same double (1.005 and 1.0049999999999999), and xUnit drops a row whose
    // arguments repeat, so without the number one case never ran and the suite gate counted it short.
    public static TheoryData<int, double, string, string> VG1bRows()
    {
        var rows = new TheoryData<int, double, string, string>();
        var n = 0;
        foreach (var row in VG1bCases) rows.Add(++n, (double)row[0], (string)row[1], (string)row[2]);
        return rows;
    }

    // The Filters sentence passes the probe's value (a double, or a decimal for a Currency column); an Equivalence cell holds the
    // "R" text of the same value. Both must read the one text, and an unchanged place reads it on both sides.
    [Theory]
    [MemberData(nameof(VG1bRows))]
    public void Every_v_g1b_case_reads_one_text_on_Filters_and_Equivalence(int number, double value, string format, string expected)
    {
        var filters = MarkNumberFormat.Display(value, format);
        var cell = MarkNumberFormat.Display(value.ToString("R", CultureInfo.InvariantCulture), format);
        var (before, after) = MarkNumberFormat.DisplayPair(value.ToString("R", CultureInfo.InvariantCulture), value, format, format);
        Assert.True(expected == filters, "case " + number + ": " + filters);
        Assert.Equal(expected, cell);
        Assert.Equal((expected, expected), (before, after));
    }

    [Theory]
    [InlineData(0, 0.00003, null, "0", "0.00003")]
    [InlineData(0, 0.00003, "#,0", "0", "0.00003")]
    [InlineData(1234.2, 1234.3, "#,0", "1,234.2", "1,234.3")]
    [InlineData(0.125, 0.12500001, "0.0%", "12.5%", "12.500001%")]
    [InlineData(628370.25, 628370.2500001, "[$$-409]#,##0.00", "$628,370.25", "$628,370.2500001")]
    [InlineData(1234567, 1234568, "#,0,", "1,234.567", "1,234.568")]
    [InlineData(0.12344999999999999, 0.12345, "", "0.12344999999999999", "0.12345")]
    [InlineData(1234.4, 5678.9, "#,0", "1,234", "5,679")]
    public void A_tiny_move_shows_enough_digits_to_tell_the_values_apart(double before, double after, string format,
        string beforeText, string afterText) =>
        Assert.Equal((beforeText, afterText), MarkNumberFormat.DisplayPair(before, after, format, format));

    // FX17 (moved from the card at the rc28 join): each side keeps its own format, and a small ratio never shows an exponent.
    [Fact]
    public void Equivalence_keeps_each_sides_own_format() =>
        Assert.Equal(("628,370", "0.0097%"), MarkNumberFormat.DisplayPair("628370", "9.707656317137992E-05", "#,0", "0.####%"));

    [Fact]
    public void Two_different_values_never_read_the_same_in_Equivalence()
    {
        var formats = new[] { null, "", "#,0", "0.00", "0.0%", "[$$-409]#,##0.00", "$#,0;($#,0)", "#,0,", "0.00E+00", "Currency", "General Number" };
        var random = new Random(20261028);
        for (var i = 0; i < 4000; i++)
        {
            var before = (random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-8, 16));
            var after = random.Next(3) switch
            {
                0 => before + Math.Pow(10, random.Next(-12, 2)) * (random.NextDouble() + 0.01),
                1 => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(before) + 1),
                _ => -before,
            };
            if (before == after) continue;
            var format = formats[random.Next(formats.Length)];
            var (b, a) = MarkNumberFormat.DisplayPair(before, after, format, format);
            Assert.True(b != a, $"{before:R} and {after:R} in \"{format}\" both read {b}");
        }
    }

    [Fact]
    public void Every_number_type_the_engine_reads_shows_through_the_same_rule()
    {
        Assert.Equal("$628,370.00", MarkNumberFormat.Display(628370m, "Currency"));
        Assert.Equal("628,370.3", MarkNumberFormat.Display(628370.3m, null));
        Assert.Equal("628,370", MarkNumberFormat.Display(628370L, ""));
        Assert.Equal("1,500", MarkNumberFormat.Display(1500, "#,0"));
        Assert.Equal("1,234.5", MarkNumberFormat.Display(1234.5f, null));
        Assert.Equal("Blank", MarkNumberFormat.Display(null, "#,0"));
        Assert.Equal("Blank", MarkNumberFormat.Display(DBNull.Value, "#,0"));
        Assert.Equal("Could not check this place: x", MarkNumberFormat.Display("Could not check this place: x", "#,0"));
        Assert.Equal("NaN", MarkNumberFormat.Display(double.NaN, "#,0"));
        Assert.Equal("0", MarkNumberFormat.Display(-0.0, null));
        Assert.Equal(("BLANK", "628,370"), MarkNumberFormat.DisplayPair("BLANK", "628370", null, null));
        // A date measure keeps the Filters sentence's date words ("every place shows 3 Jan 2018").
        Assert.Equal("3 Jan 2018", MeasureMarkJudge.CellText(MarkNumberFormat.Display(new DateTime(2018, 1, 3), "dd/mm/yyyy")));
    }

    // fx28 (v-fx27 polish a): the gap a Trusted number or SQL mark names reads in the measure's format, also when the two numbers
    // have different signs, and a gap under the format's rounding shows enough places to be seen: never "0 higher".
    [Theory]
    [InlineData("1000", "-5", "[$$-409]#,##0.00", "$1,005.00")]
    [InlineData("1000", "-5", "Currency", "$1,005.00")]
    [InlineData("-5", "1000", "Currency", "$1,005.00")]
    [InlineData("628370", "628365", "Currency", "$5.00")]
    [InlineData("628370", "628370.001", "Currency", "$0.001")]
    [InlineData("628370", "628370.0004", "[$$-409]#,##0.00", "$0.0004")]
    [InlineData("0.1234", "0.12341", "0.00%", "0.001%")]
    [InlineData("0.12", "0.17", "0.0%", "5.0%")]
    [InlineData("1000.4", "1000.2", "#,0", "0.2")]
    [InlineData("100", "95", "", "5")]
    [InlineData("0.1", "0.3", null, "0.2")]
    [InlineData("-1234.5", "1234.5", "#,0.00;(#,0.00)", "2,469.00")]
    public void A_gap_reads_in_the_measure_format_and_never_reads_zero(string before, string after, string format, string expected) =>
        Assert.Equal(expected, MarkNumberFormat.Gap(before, after, format));

    [Fact]
    public void A_gap_is_null_when_there_is_none_or_a_side_is_not_a_number()
    {
        Assert.Null(MarkNumberFormat.Gap("5", "5", "0"));
        Assert.Null(MarkNumberFormat.Gap(null, "5", "0"));
        Assert.Null(MarkNumberFormat.Gap("5", "ERROR: x", "0"));
        // A cell gets its gap text with its texts; a Filters count has none, and a saved record without one gets it on read.
        var trusted = new MeasureMark { Name = "Trusted number", State = "suspect",
            Cells = new[] { new MeasureMarkCell { Context = "Grand total", Before = "1000", After = "-5" } } };
        MarkNumberFormat.FillCells(trusted, "Currency");
        Assert.Equal(("$1,000.00", "($5.00)", "$1,005.00"), (trusted.Cells[0].BeforeText, trusted.Cells[0].AfterText, trusted.Cells[0].GapText));
        var saved = new MeasureMark { Name = "SQL", State = "suspect",
            Cells = new[] { new MeasureMarkCell { Context = "Grand total", Before = "628370", After = "628370.001", BeforeText = "$628,370.00", AfterText = "$628,370.00" } } };
        Assert.Equal("$0.001", MarkNumberFormat.FillCells(saved, "Currency").Cells[0].GapText);
        var counts = new MeasureMark { Name = "Filters", State = "suspect",
            Cells = new[] { new MeasureMarkCell { Context = "Product", Before = "10", After = "4" } } };
        Assert.Null(MarkNumberFormat.FillCells(counts, "Currency").Cells[0].GapText);
    }
}
