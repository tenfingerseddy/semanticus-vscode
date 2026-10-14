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
    /// <summary>
    /// A report-level measure (reportExtensions.json) holds DAX the report owns. If that DAX reads a model column, a
    /// rename or delete of the column breaks the report, so the column must count as report use everywhere: the unused
    /// sweep, impact, and the rename warning. Before this, only the measure's NAME was listed and its formula was never read.
    /// </summary>
    public sealed class ReportMeasureUseTests
    {
        private sealed class ProTier : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static (LocalEngine Engine, SessionManager Sessions, string Root, string Model) Make()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-rmeasure-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new ProTier(), root), sessions, root, model);
        }

        // A PBIR report with NO visual at all, only a report measure whose DAX is the given text (real reportExtensions shape).
        internal static string WriteReportWithMeasure(string root, string folder, string table, string measureName, string dax)
        {
            var reportRoot = Path.Combine(root, folder);
            var def = Path.Combine(reportRoot, "R.Report", "definition");
            Directory.CreateDirectory(def);
            File.WriteAllText(Path.Combine(def, "report.json"), "{}");
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                name = "extension",
                entities = new[] { new { name = table, measures = new[] { new { name = measureName, dataType = "Double", expression = dax } } } },
            });
            File.WriteAllText(Path.Combine(def, "reportExtensions.json"), json);
            return reportRoot;
        }

        private static string Quote(string table) => "'" + table.Replace("'", "''") + "'";

        [Fact]
        public async Task A_column_used_only_inside_a_report_measure_is_not_unused_is_in_impact_and_its_rename_names_the_report_measure()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var col = await x.Engine.CreateColumnAsync("table:" + table, "RM Col", "Decimal", "RM Col", "human");
                    await x.Engine.SetObjectPropertyAsync(col, "IsHidden", "true", "human");
                    var path = WriteReportWithMeasure(x.Root, "Rm", table, "RM Margin", "SUM ( " + Quote(table) + "[RM Col] )");

                    // Model-only, nothing reads the column.
                    Assert.Contains((await x.Engine.UnusedObjectsAsync()).Items, i => i.Ref == col && i.Verdict == "safe");

                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Sales files", Path = path } }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    // Report-aware: the report measure reads it, so it is not on the unused list as safe.
                    Assert.DoesNotContain((await x.Engine.UnusedObjectsAsync()).Items, i => i.Ref == col && i.Verdict == "safe");

                    var impact = await x.Engine.ImpactAssessmentAsync(new ImpactAssessmentRequest { ObjectRef = col, Intent = "rename" });
                    var hit = Assert.Single(impact.ReportImpact);
                    Assert.Equal("Sales files", hit.Name);
                    Assert.Contains(col, hit.UsedRefs);
                    Assert.Contains("RM Margin", hit.ReportMeasures);

                    var renamed = await x.Engine.RenameObjectAsync(col, "RM Col Two", "human");
                    Assert.True(renamed.Changed);
                    Assert.Contains("Sales files", renamed.Warning);
                    Assert.Contains("RM Margin", renamed.Warning);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task A_report_measure_that_reads_nothing_in_the_model_keeps_a_same_named_object_from_being_safe()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var ghost = await x.Engine.CreateMeasureAsync("table:" + table, "RM Ghost", "1", "human");
                    // With no report checked or declared absent, an unused measure is "caution" (finish3); the person says there are no reports and it is "safe".
                    Assert.Contains((await x.Engine.UnusedObjectsAsync()).Items, i => i.Ref == ghost && i.Verdict == "caution");
                    await x.Engine.SetReportScopeAsync(System.Array.Empty<ReportScopeChoice>(), "human");
                    Assert.Contains((await x.Engine.UnusedObjectsAsync()).Items, i => i.Ref == ghost && i.Verdict == "safe");

                    var path = WriteReportWithMeasure(x.Root, "RmGhost", table, "RM Uses Ghost", "SUM ( 'No Such Table'[RM Ghost] )");
                    // One card shows a real measure, so the report matches the model and only the ghost name is the gap.
                    var shown = (await x.Engine.ListMeasuresAsync()).First();
                    var visualDir = Path.Combine(path, "R.Report", "definition", "pages", "P", "visuals", "V");
                    Directory.CreateDirectory(visualDir);
                    File.WriteAllText(Path.Combine(visualDir, "visual.json"), "{ \"visual\": { \"visualType\": \"card\", \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " +
                        "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + shown.Table.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\" } }, \"Property\": \"" + shown.Name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\" } } } ] } } } } }");
                    await x.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Ghost files", Path = path } }, "human");
                    await x.Engine.CheckReportsAsync(null, true, null, null, null, "human");

                    var item = Assert.Single((await x.Engine.UnusedObjectsAsync()).Items, i => i.Ref == ghost);
                    Assert.Equal("caution", item.Verdict);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }

        [Fact]
        public async Task Rename_with_no_report_checked_keeps_the_plain_sentence()
        {
            var x = Make();
            try
            {
                using (x.Engine)
                {
                    await x.Engine.OpenAsync(x.Model);
                    var table = (await x.Engine.ListMeasuresAsync()).First().Table;
                    var col = await x.Engine.CreateColumnAsync("table:" + table, "RM Plain", "Decimal", "RM Plain", "human");
                    var renamed = await x.Engine.RenameObjectAsync(col, "RM Plain Two", "human");
                    Assert.Equal("Names in reports are not rewritten. Update any report that uses this name.", renamed.Warning);
                }
            }
            finally { x.Sessions.Dispose(); try { Directory.Delete(x.Root, true); } catch { } }
        }
    }
}
