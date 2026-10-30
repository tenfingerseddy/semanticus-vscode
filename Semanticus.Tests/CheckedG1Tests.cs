using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
// G1 (f) and v-g1: the Filters sentence names its number through MarkNumberFormat, as the Equivalence cells do. The G1 timing
// tests (queue position, singular seconds, "changed" after timing) belong to the state lane, L1, and left this file on m18/l3.
public sealed class CheckedG1Tests
{
    private const string Reference = "measure:Sales/Total Sales";
    private static void Evidence(string name, object value) => File.WriteAllText(
        Path.Combine(Path.GetTempPath(), "g1-" + name + ".json"), JsonSerializer.Serialize(value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    [Theory]
    [InlineData("#,0", 628370, "628,370")]
    [InlineData("0.00%", 0.125, "12.50%")]
    [InlineData("$#,0.00;($#,0.00)", -1234.5, "($1,234.50)")]
    [InlineData("#,0", 1234.4999999999998, "1,234")]
    [InlineData("Currency", 628370d, "$628,370.00")]
    [InlineData("", 0.00003, "0.00003")]
    public async Task Filters_use_the_measure_format(string format, double value, string expected)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso filter clear", m => {
            var measure = m.Tables["Sales"].Measures["Total Sales"];
            measure.Expression = "CALCULATE(SUM('Sales'[Amount]), ALL('Product'))";
            measure.FormatString = format;
        });
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", query => {
            var result = f.Query(query);
            var index = Array.FindIndex(result.Columns, c => c.Name == "v");
            if (index >= 0) foreach (var row in result.Rows) row[index] = value;
            return result;
        }));
        await f.Engine.GetMeasureMarksAsync(Reference, refresh: true);
        var mark = Assert.Single((await MeasureMarksTests.Finish(f.Engine, Reference)).Marks, m => m.Name == "Filters");
        if (format == "#,0") Evidence("filter", mark);
        Assert.Equal("info", mark.State);
        Assert.Contains(expected + ".", mark.Sentence);
    }

    // V-G1: a format the .NET formatter does not understand must not replace the number with its own letters.
    // Equivalence shows "628,370" for each of these formats, so the Filters sentence must name the same number.
    [Theory]
    [InlineData("Currency")]
    [InlineData("Standard")]
    [InlineData("[$$-409]#,##0.00")]
    [InlineData("")]
    public async Task Filters_show_a_readable_number_for_every_format(string format)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso filter clear", m => {
            var measure = m.Tables["Sales"].Measures["Total Sales"];
            measure.Expression = "CALCULATE(SUM('Sales'[Amount]), ALL('Product'))";
            measure.FormatString = format;
        });
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", query => {
            var result = f.Query(query);
            var index = Array.FindIndex(result.Columns, c => c.Name == "v");
            if (index >= 0) foreach (var row in result.Rows) row[index] = 628370d;
            return result;
        }));
        await f.Engine.GetMeasureMarksAsync(Reference, refresh: true);
        var mark = Assert.Single((await MeasureMarksTests.Finish(f.Engine, Reference)).Marks, m => m.Name == "Filters");
        Assert.Equal("info", mark.State);
        Assert.True(mark.Sentence.Contains("628,370") && !mark.Sentence.Contains("Currency")
            && !mark.Sentence.Contains("Standard") && !mark.Sentence.Contains("[$"), mark.Sentence);
    }
}
