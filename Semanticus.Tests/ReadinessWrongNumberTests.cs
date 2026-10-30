using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests;

public sealed class ReadinessWrongNumberTests
{
    private readonly ITestOutputHelper _output;
    public ReadinessWrongNumberTests(ITestOutputHelper output) => _output = output;
    private static RuleEvaluation Eval(SessionManager sessions, string id) =>
        ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);

    private static async Task<(SessionManager sessions, LocalEngine engine, string sales)> GradeProbeModel()
    {
        var sessions = new SessionManager();
        var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.CreateModelAsync("Probe", 1604);
        var sales = await engine.CreateTableAsync("Sales", "human");
        foreach (var column in new[] { "Profit", "Revenue", "Qty", "Price", "Rate", "Cost", "Balance", "Amount", "OnHandQty" })
            await engine.CreateColumnAsync(sales, column, "Decimal", column, "human");
        var dates = await engine.CreateTableAsync("Date", "human");
        await engine.CreateColumnAsync(dates, "Date", "DateTime", "Date", "human");
        await engine.CreateColumnAsync(dates, "Year", "Int64", "Year", "human");
        await engine.CreateColumnAsync(dates, "YearLabel", "String", "YearLabel", "human");
        await engine.CreateMeasureAsync(sales, "Sales Amount", "SUM(Sales[Amount])", "human");
        return (sessions, engine, sales);
    }

    // Adopted from the verifier: compare equal-sized models to isolate the BestPractice score.
    [Theory]
    [InlineData("DAX-DATE-LITERAL", "CALCULATE([Sales Amount], 'Date'[Date] = MAX('Date'[Date]))")]
    [InlineData("SEMI-ADDITIVE-SUM", "CALCULATE(SUM(Sales[Balance]), LASTDATE('Date'[Date]))")]
    [InlineData("AVG-OF-RATIO", "DIVIDE(SUM(Sales[Profit]), SUM(Sales[Revenue]))")]
    public async Task Probe_clean_construct_does_not_raise_the_grade(string rule, string dax)
    {
        var (sessions, engine, sales) = await GradeProbeModel();
        using (engine)
        {
            var before = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
            await engine.CreateMeasureAsync(sales, "Clean thing", dax, "human");
            var after = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
            var (neutralSessions, neutralEngine, neutralSales) = await GradeProbeModel();
            using (neutralEngine)
            {
                await neutralEngine.CreateMeasureAsync(neutralSales, "Clean thing", "SUM(Sales[Revenue])", "human");
                var neutral = new ReadinessAnalyzer().Analyze(neutralSessions.Current.Model);
                _output.WriteLine($"{rule}: raw {before.RawOverall:F3} -> {after.RawOverall:F3}; neutral {neutral.RawOverall:F3}");
                Assert.True(after.RawOverall <= neutral.RawOverall + 1e-9,
                    $"{rule}: clean construct raised raw overall {neutral.RawOverall:F3} -> {after.RawOverall:F3}");
                Assert.Equal(0, Eval(sessions, rule).Applicable);
                Assert.Empty(Eval(sessions, rule).Violations);
                Assert.DoesNotContain(after.Categories, c => c.Category == "BestPractice" && c.Applicable > 0);
            }
        }
    }

    [Theory]
    [InlineData("DAX-DATE-LITERAL", "CALCULATE([Sales Amount], 'Date'[Year] = 2024)")]
    [InlineData("SEMI-ADDITIVE-SUM", "SUM(Sales[Balance])")]
    public async Task Wrong_number_violation_docks_the_grade(string rule, string dax)
    {
        var (sessions, engine, sales) = await GradeProbeModel();
        using (engine)
        {
            var measure = await engine.CreateMeasureAsync(sales, "Check", "SUM(Sales[Revenue])", "human");
            var clean = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
            await engine.SetDaxAsync(measure, dax, "human");
            var bad = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
            Assert.Equal(1, Eval(sessions, rule).Applicable);
            Assert.Single(Eval(sessions, rule).Violations);
            Assert.True(bad.RawOverall < clean.RawOverall);
        }
    }

    [Fact]
    public async Task Average_of_ratios_fires_and_ratio_of_totals_clears()
    {
        var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.CreateModelAsync("RatioCheck", 1604);
        var table = await engine.CreateTableAsync("Sales", "human");
        await engine.CreateColumnAsync(table, "Profit", "Decimal", "Profit", "human");
        await engine.CreateColumnAsync(table, "Revenue", "Decimal", "Revenue", "human");
        Assert.Equal(0, Eval(sessions, "AVG-OF-RATIO").Applicable);
        var measure = await engine.CreateMeasureAsync(table, "Margin %", "AVERAGEX(Sales, DIVIDE(Sales[Profit], Sales[Revenue]))", "human");
        var bad = Eval(sessions, "AVG-OF-RATIO");
        Assert.Equal(1, bad.Applicable);
        Assert.Equal(measure, Assert.Single(bad.Violations).ObjectRef);
        await engine.SetDaxAsync(measure, "DIVIDE(SUM(Sales[Profit]), SUM(Sales[Revenue]))", "human");
        Assert.Empty(Eval(sessions, "AVG-OF-RATIO").Violations);
        Assert.Equal(0, Eval(sessions, "AVG-OF-RATIO").Applicable);
        await engine.CreateColumnAsync(table, "Row Margin %", "Decimal", "Row Margin %", "human");
        await engine.CreateMeasureAsync(table, "Average margin", "AVERAGE(Sales[Row Margin %])", "human");
        Assert.Single(Eval(sessions, "AVG-OF-RATIO").Violations);
    }

    [Fact]
    public async Task Snapshot_sum_warns_and_last_date_clears()
    {
        var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.CreateModelAsync("BalanceCheck", 1604);
        var table = await engine.CreateTableAsync("Inventory", "human");
        var dates = await engine.CreateTableAsync("Date", "human");
        await engine.CreateColumnAsync(dates, "Date", "DateTime", "Date", "human");
        await engine.CreateColumnAsync(table, "Balance", "Decimal", "Balance", "human");
        var measure = await engine.CreateMeasureAsync(table, "Closing Balance", "SUM(Inventory[Balance])", "human");
        var bad = Eval(sessions, "SEMI-ADDITIVE-SUM");
        Assert.Equal(1, bad.Applicable);
        Assert.Contains("may", Assert.Single(bad.Violations).Message);
        await engine.SetDaxAsync(measure, "CALCULATE(SUM(Inventory[Balance]), LASTDATE('Date'[Date]))", "human");
        Assert.Empty(Eval(sessions, "SEMI-ADDITIVE-SUM").Violations);
        Assert.Equal(0, Eval(sessions, "SEMI-ADDITIVE-SUM").Applicable);
    }

    [Fact]
    public async Task Fixed_year_filter_warns_and_data_anchor_clears()
    {
        var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.CreateModelAsync("DateCheck", 1604);
        var table = await engine.CreateTableAsync("Sales", "human");
        await engine.CreateMeasureAsync(table, "Sales Amount", "1", "human");
        var dates = await engine.CreateTableAsync("Date", "human");
        await engine.CreateColumnAsync(dates, "Date", "DateTime", "Date", "human");
        await engine.CreateColumnAsync(dates, "Year", "Int64", "Year", "human");
        var measure = await engine.CreateMeasureAsync(table, "Sales in 2024", "CALCULATE([Sales Amount], 'Date'[Year] = 2024)", "human");
        var bad = Eval(sessions, "DAX-DATE-LITERAL");
        Assert.Equal(1, bad.Applicable);
        Assert.Single(bad.Violations);
        await engine.SetDaxAsync(measure, "CALCULATE([Sales Amount], 'Date'[Date] = MAX('Date'[Date]))", "human");
        Assert.Empty(Eval(sessions, "DAX-DATE-LITERAL").Violations);
        Assert.Equal(0, Eval(sessions, "DAX-DATE-LITERAL").Applicable);
    }

    [Fact]
    public async Task Key_integrity_reports_counts_and_is_dormant_without_live_counts()
    {
        var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.CreateModelAsync("KeyCheck", 1604);
        var fact = await engine.CreateTableAsync("Sales", "human");
        var dim = await engine.CreateTableAsync("Product", "human");
        var fk = await engine.CreateColumnAsync(fact, "ProductKey", "Int64", "ProductKey", "human");
        var pk = await engine.CreateColumnAsync(dim, "ProductKey", "Int64", "ProductKey", "human");
        var relation = await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
        var rule = ReadinessRuleSet.LiveRules(new ReadinessLiveStats()).Single(r => r.Id == "KEY-INTEGRITY");
        var result = rule.Evaluate(sessions.Current.Model);
        Assert.Equal(0, result.Applicable);
        Assert.Empty(result.Violations);
        var counts = new ReadinessLiveStats();
        counts.KeyIntegrity[relation] = new KeyIntegrityCounts { UnmatchedRows = 3, DuplicateRows = 2 };
        var bad = ReadinessRuleSet.LiveRules(counts).Single(r => r.Id == "KEY-INTEGRITY").Evaluate(sessions.Current.Model);
        Assert.Equal(1, bad.Applicable);
        Assert.Contains("3 rows", Assert.Single(bad.Violations).Message);
        Assert.Contains("2 extra rows", bad.Violations[0].Message);
        counts.KeyIntegrity[relation] = new KeyIntegrityCounts();
        Assert.Empty(ReadinessRuleSet.LiveRules(counts).Single(r => r.Id == "KEY-INTEGRITY").Evaluate(sessions.Current.Model).Violations);
    }

    [Fact]
    public async Task Ratio_offer_reads_before_and_after_and_keeps_the_formula_until_either_door_chooses()
    {
        var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.CreateModelAsync("RatioOffer", 1604);
        var table = await engine.CreateTableAsync("Sales", "human");
        await engine.CreateColumnAsync(table, "Net Sales Amount", "Decimal", "Net Sales Amount", "human");
        await engine.CreateColumnAsync(table, "CostAmount", "Decimal", "CostAmount", "human");
        const string original = "AVERAGEX(Sales, DIVIDE('Sales'[Net Sales Amount] - Sales[CostAmount], 'Sales'[Net Sales Amount]))";
        var measure = await engine.CreateMeasureAsync(table, "Margin %", original, "human");
        var queries = new List<string>();
        engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "ratio-offer", "RatioOffer", q =>
        {
            queries.Add(q);
            return new ResultSet { Rows = new[] { new object[] { "Electronics", 0.1655, 0.0583 } }, RowCount = 1 };
        }));
        var revision = sessions.Current.Revision;
        var prompt = await engine.GetFixPromptAsync("AVG-OF-RATIO", measure);
        var offer = Assert.IsType<RatioFixOffer>(prompt.RatioOffer);
        Assert.Null(offer.Problem);
        Assert.Equal("DIVIDE(SUM('Sales'[Net Sales Amount]) - SUM('Sales'[CostAmount]), SUM('Sales'[Net Sales Amount]))", offer.Expression);
        Assert.Equal(0.1655, Assert.Single(offer.Rows).Before);
        Assert.Equal(0.0583, offer.Rows[0].After);
        Assert.Contains("before", prompt.Prompt);
        Assert.Contains("after", prompt.Prompt);
        Assert.Single(queries);
        Assert.Equal(revision, sessions.Current.Revision);
        Assert.Equal(original, sessions.Current.Model.Tables["Sales"].Measures["Margin %"].Expression);
        foreach (var origin in new[] { "human", "agent" })
        {
            var applied = origin == "agent"
                ? await McpTools.UpdateMeasure(engine, measure, offer.Expression, sessions.Current.Id)
                : await engine.SetDaxAsync(measure, offer.Expression, origin, sessions.Current.Id, sessions.Current.Revision, original);
            Assert.True(applied.Changed);
            Assert.Empty(Eval(sessions, "AVG-OF-RATIO").Violations);
            await engine.UndoAsync(origin);
            Assert.Single(Eval(sessions, "AVG-OF-RATIO").Violations);
        }
    }

    [Fact]
    public async Task Live_key_scan_transfers_counts_only_and_reports_a_failed_count_as_unknown()
    {
        var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.CreateModelAsync("LiveKeys", 1604);
        var fact = await engine.CreateTableAsync("Sales", "human");
        var dim = await engine.CreateTableAsync("Product", "human");
        var fk = await engine.CreateColumnAsync(fact, "ProductKey", "Int64", "ProductKey", "human");
        var pk = await engine.CreateColumnAsync(dim, "ProductKey", "Int64", "ProductKey", "human");
        await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
        var queries = new List<string>();
        var failed = false;
        var clean = false;
        var incomplete = false;
        engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "live-keys", "LiveKeys", q =>
        {
            queries.Add(q);
            if (q.Contains("COLUMNSTATISTICS", StringComparison.Ordinal)) return new ResultSet();
            Assert.Contains("COUNTROWS", q);
            Assert.DoesNotContain("TOPN", q);
            return failed ? new ResultSet { Error = "Count unavailable" } : new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "[UnmatchedRows]" }, new ColumnDef { Name = "[DuplicateRows]" } },
                Rows = new[] { new object[] { clean ? 0L : 7L, clean ? 0L : 2L } }, RowCount = 1,
                Truncated = incomplete, Error = ""
            };
        }));
        var revision = sessions.Current.Revision;
        var bad = await engine.AiReadinessScanLiveAsync();
        var finding = Assert.Single(bad.Findings.Where(f => f.RuleId == "KEY-INTEGRITY"));
        Assert.Contains("7 rows", finding.Message);
        Assert.Contains("2 extra rows", finding.Message);
        clean = true;
        Assert.DoesNotContain((await engine.AiReadinessScanLiveAsync()).Findings, f => f.RuleId == "KEY-INTEGRITY");
        failed = true;
        Assert.Contains((await engine.AiReadinessScanLiveAsync()).RuleErrors, e => e.Contains("Count unavailable", StringComparison.Ordinal));
        failed = false;
        incomplete = true;
        Assert.Contains((await engine.AiReadinessScanLiveAsync()).RuleErrors, e => e.Contains("complete result", StringComparison.Ordinal));
        Assert.Equal(revision, sessions.Current.Revision);
        Assert.All(queries, q => Assert.StartsWith("EVALUATE", q));
    }

    [Fact]
    public async Task Formula_checks_ignore_comments_labels_and_normal_sums_and_follow_balance_dependencies()
    {
        var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.CreateModelAsync("QuietModel", 1604);
        var table = await engine.CreateTableAsync("Sales", "human");
        var dates = await engine.CreateTableAsync("Date", "human");
        await engine.CreateColumnAsync(dates, "Date", "DateTime", "Date", "human");
        await engine.CreateColumnAsync(dates, "Year", "Int64", "Year", "human");
        await engine.CreateColumnAsync(table, "Revenue", "Decimal", "Revenue", "human");
        await engine.CreateColumnAsync(table, "Balance", "Decimal", "Balance", "human");
        await engine.CreateMeasureAsync(table, "Revenue total", "SUM(Sales[Revenue])", "human");
        await engine.CreateMeasureAsync(table, "Average revenue", "AVERAGE(Sales[Revenue]) /* DIVIDE(Sales[Balance],Sales[Revenue]) */", "human");
        await engine.CreateMeasureAsync(table, "Label", "\"AVERAGEX(Sales,DIVIDE(1,2)) DATE(2024,1,1)\"", "human");
        foreach (var id in new[] { "AVG-OF-RATIO", "SEMI-ADDITIVE-SUM", "DAX-DATE-LITERAL" })
        { Assert.Empty(Eval(sessions, id).Violations); Assert.Equal(0, Eval(sessions, id).Applicable); }
        await engine.CreateMeasureAsync(table, "Stock", "SUM(Sales[Balance])", "human");
        await engine.CreateMeasureAsync(table, "Stock total", "[Stock]", "human");
        await engine.CreateMeasureAsync(table, "Last stock", "CALCULATE([Stock], LASTDATE('Date'[Date]))", "human");
        Assert.Equal(2, Eval(sessions, "SEMI-ADDITIVE-SUM").Applicable);
        Assert.Equal(2, Eval(sessions, "SEMI-ADDITIVE-SUM").Violations.Count);
        await engine.CreateMeasureAsync(table, "Fixed date", "CALCULATE([Revenue total], 'Date'[Date] >= DATE(2024,1,1))", "human");
        await engine.CreateMeasureAsync(table, "Fixed text date", "CALCULATE([Revenue total], 'Date'[Date] >= \"2024-01-01\")", "human");
        Assert.Equal(2, Eval(sessions, "DAX-DATE-LITERAL").Violations.Count);
    }

    public sealed class NorthstarFactAttribute : FactAttribute
    {
        public NorthstarFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SEMANTICUS_R1_LIVE") != "1")
                Skip = "Read-only Northstar check. Set SEMANTICUS_R1_LIVE=1 with the approved sign-in wrapper.";
        }
    }

    [NorthstarFact]
    public async Task Northstar_read_only_scan_and_ratio_offer_match_the_live_numbers()
    {
        // The wrapper supplies a memory-only settings path because the test host isolates cloud sign-in variables.
        var settingsPath = Environment.GetEnvironmentVariable("SEMANTICUS_LIVE_SP_ENV_FILE");
        Assert.NotNull(settingsPath);
        foreach (var line in File.ReadAllLines(settingsPath!))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) Environment.SetEnvironmentVariable(line.Substring(0, eq), line.Substring(eq + 1));
        }
        var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
        await engine.OpenLiveAsync("powerbi://api.powerbi.com/v1.0/myorg/FrameworkTesting", "Northstar Sales Working", "serviceprincipal", null, null);
        var revision = sessions.Current.Revision;
        var card = await engine.AiReadinessScanLiveAsync();
        foreach (var id in new[] { "AVG-OF-RATIO", "KEY-INTEGRITY", "SEMI-ADDITIVE-SUM", "DAX-DATE-LITERAL" })
        {
            var findings = card.Findings.Where(f => f.RuleId == id).ToArray();
            _output.WriteLine(id + ": " + findings.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var f in findings) _output.WriteLine(f.ObjectName + ": " + f.Message);
        }
        foreach (var error in card.RuleErrors) _output.WriteLine("Rule error: " + error);
        Assert.Empty(card.RuleErrors);
        Assert.Contains(card.Findings, f => f.RuleId == "AVG-OF-RATIO" && f.ObjectName == "Margin %");
        var prompt = await engine.GetFixPromptAsync("AVG-OF-RATIO", "measure:Sales/Margin %");
        Assert.Null(prompt.RatioOffer.Problem);
        foreach (var row in prompt.RatioOffer.Rows)
            _output.WriteLine(row.Scope + ": " + row.Before?.ToString("G17", CultureInfo.InvariantCulture) + " -> " + row.After?.ToString("G17", CultureInfo.InvariantCulture));
        var electronics = Assert.Single(prompt.RatioOffer.Rows.Where(r => r.Scope == "Electronics"));
        Assert.InRange(electronics.Before!.Value, 0.1654, 0.1656);
        Assert.InRange(electronics.After!.Value, 0.0582, 0.0584);
        Assert.Equal(revision, sessions.Current.Revision);

        // Northstar has no balance or fixed-window measures and has clean relationships today.
        // Change only its downloaded local metadata to exercise those cases against real read-only data.
        await sessions.Current.MutateAsync("human", "Readiness fixtures on the local copy", model =>
        {
            var sales = model.Tables["Sales"];
            var amount = sales.Columns.First(c => c.Name == "Net Sales Amount" || c.Name == "NetSalesAmount");
            sales.AddMeasure("Closing balance fixture", "SUM(" + WrongNumberReadiness.ColumnDax(amount) + ")");
            sales.AddMeasure("Fixed year fixture", "CALCULATE([Net Sales], 'Date'[Year] = 2024)");
            var relation = model.Relationships.OfType<TabularEditor.TOMWrapper.SingleColumnRelationship>()
                .First(r => r.FromTable == sales && r.ToTable.Name == "Product");
            relation.ToColumn = model.Tables["Date"].Columns["Year"];
        });
        var localFixture = await engine.AiReadinessScanLiveAsync();
        Assert.Empty(localFixture.RuleErrors);
        foreach (var id in new[] { "AVG-OF-RATIO", "KEY-INTEGRITY", "SEMI-ADDITIVE-SUM", "DAX-DATE-LITERAL" })
        {
            Assert.Contains(localFixture.Findings, f => f.RuleId == id);
            _output.WriteLine("Local copy fixture " + id + ": " + localFixture.Findings.Count(f => f.RuleId == id));
        }
        await engine.UndoAsync("human");
        await engine.SetDaxAsync("measure:Sales/Margin %", prompt.RatioOffer.Expression, "human");
        var cleanCopy = await engine.AiReadinessScanLiveAsync();
        Assert.Empty(cleanCopy.RuleErrors);
        foreach (var id in new[] { "AVG-OF-RATIO", "KEY-INTEGRITY", "SEMI-ADDITIVE-SUM", "DAX-DATE-LITERAL" })
        {
            Assert.DoesNotContain(cleanCopy.Findings, f => f.RuleId == id);
            _output.WriteLine("Clean local copy " + id + ": 0");
        }
    }
}
