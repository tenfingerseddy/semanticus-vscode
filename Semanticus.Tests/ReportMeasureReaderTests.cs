using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.Lineage;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The report-measure reader and the plain account check_reports gives of it.</summary>
    public sealed class ReportMeasureReaderTests
    {
        private sealed class ProTier : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private const string Extensions = @"{
          ""name"": ""extension"",
          ""entities"": [ { ""name"": ""Sales"", ""measures"": [
            { ""name"": ""Margin %"", ""dataType"": ""Double"",
              ""expression"": ""DIVIDE ( [Profit], SUM ( 'Sales'[Amount] ) ) + SUM ( Orders[Qty] ) // [In A Comment]\n & \""[In A String]\"""",
              ""references"": { ""measures"": [ { ""entity"": ""Sales"", ""name"": ""Profit"" } ] } },
            { ""name"": ""Dates"", ""expression"": ""CALCULATE ( [Margin %], 'Date'[Year] ) + [Date].[Year]"" }
          ] } ]
        }";

        [Fact]
        public void Reads_the_DAX_of_each_report_measure_into_model_names()
        {
            var res = ReportDefinitionReader.Parse(new[] { ("definition/reportExtensions.json", Extensions) });
            Assert.Equal(2, res.ReportMeasures.Count);
            var m = res.ReportMeasures.Single(x => x.Name == "Margin %");
            Assert.Equal("Sales", m.Entity);
            Assert.Contains(m.Refs, r => r.Table == null && r.Name == "Profit");
            Assert.Contains(m.Refs, r => r.Table == "Sales" && r.Name == "Amount");
            Assert.Contains(m.Refs, r => r.Table == "Orders" && r.Name == "Qty");
            Assert.DoesNotContain(m.Refs, r => r.Name.Contains("In A"));
            Assert.Equal(3, m.Refs.Count);   // Profit once, though its `references` list repeats it
            var d = res.ReportMeasures.Single(x => x.Name == "Dates");
            Assert.Contains(d.Refs, r => r.Table == "Date" && r.Name == "Year");
            Assert.DoesNotContain(d.Refs, r => r.Table == null && r.Name == "Year");
        }

        [Fact]
        public async Task Check_reports_says_how_many_report_measures_it_read_and_how_many_references_it_checked()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-rmeasure-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            var sessions = new SessionManager();
            try
            {
                using var engine = new LocalEngine(sessions, new ProTier(), root);
                await engine.OpenAsync(model);
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var col = await engine.CreateColumnAsync("table:" + table, "RM Count", "Decimal", "RM Count", "human");
                var path = ReportMeasureUseTests.WriteReportWithMeasure(root, "Rm", table, "RM Total", "SUM ( '" + table.Replace("'", "''") + "'[RM Count] )");
                await engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Counted", Path = path } }, "human");
                var result = await engine.CheckReportsAsync(null, true, null, null, null, "human");

                Assert.Equal(1, result.Usage.ReportMeasuresRead);
                Assert.Equal(1, result.Usage.ReportMeasureReferencesChecked);
                Assert.Equal("Read 1 report measure and checked 1 reference in their formulas.", result.ReportMeasures);
                Assert.Equal("checked", Assert.Single(result.Reports).State);
            }
            finally { sessions.Dispose(); try { Directory.Delete(root, true); } catch { } }
        }
    }
}
