using System;
using System.IO;
using System.Linq;
using Semanticus.Engine.Lineage;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The PBIR field-reference parser (Phase-3 "Measure Killer" core) — pure, fixture-driven, fully offline. Proves
    /// it extracts model field usage from EVERY reference location (projections, an Aggregation wrapper, a
    /// Source-alias filter resolved via the From clause, and a Schema:"extension" report-level measure) so the
    /// report-aware safe-to-remove sweep never over-reports "unused". Spec: docs/lineage-impact-plan.md
    /// "CONFIRMED: the PBIR field-reference schema".
    /// </summary>
    public sealed class ReportDefinitionReaderTests
    {
        // A representative visual.json exercising each reference shape + location.
        private const string VisualJson = @"{
          ""$schema"": ""https://developer.microsoft.com/json-schemas/fabric/item/report/definition/visualContainer/2.4.0/schema.json"",
          ""name"": ""v1"",
          ""visual"": {
            ""visualType"": ""barChart"",
            ""query"": {
              ""queryState"": {
                ""Y"": { ""projections"": [
                  { ""field"": { ""Measure"": { ""Expression"": { ""SourceRef"": { ""Entity"": ""Sales"" } }, ""Property"": ""Total Sales"" } }, ""queryRef"": ""Sales.Total Sales"" },
                  { ""field"": { ""Aggregation"": { ""Expression"": { ""Column"": { ""Expression"": { ""SourceRef"": { ""Entity"": ""Sales"" } }, ""Property"": ""Amount"" } }, ""Function"": 0 } }, ""queryRef"": ""Sum(Sales.Amount)"" },
                  { ""field"": { ""Measure"": { ""Expression"": { ""SourceRef"": { ""Schema"": ""extension"", ""Entity"": ""Sales"" } }, ""Property"": ""Report Margin"" } } }
                ] },
                ""Category"": { ""projections"": [
                  { ""field"": { ""Column"": { ""Expression"": { ""SourceRef"": { ""Entity"": ""Customer"" } }, ""Property"": ""City"" } } }
                ] }
              }
            }
          },
          ""filterConfig"": {
            ""filters"": [ {
              ""filter"": {
                ""From"": [ { ""Name"": ""c"", ""Entity"": ""Customer"", ""Type"": 0 } ],
                ""Where"": [ { ""Condition"": { ""In"": { ""Expressions"": [
                  { ""Column"": { ""Expression"": { ""SourceRef"": { ""Source"": ""c"" } }, ""Property"": ""Country"" } }
                ] } } } ]
              }
            } ]
          }
        }";

        [Fact]
        public void Parses_every_field_reference_location_shape()
        {
            var res = ReportDefinitionReader.Parse(new[] { VisualJson });
            var f = res.Fields;

            // projection: model measure
            Assert.Contains(f, r => r.Kind == "measure" && !r.IsExtension && r.Entity == "Sales" && r.Property == "Total Sales");
            // projection: an Aggregation wrapping a column ⇒ the inner column is captured
            Assert.Contains(f, r => r.Kind == "column" && r.Entity == "Sales" && r.Property == "Amount");
            // projection in a different data well
            Assert.Contains(f, r => r.Kind == "column" && r.Entity == "Customer" && r.Property == "City");
            // filter using a From-alias Source ⇒ resolved to Customer
            Assert.Contains(f, r => r.Kind == "column" && r.Entity == "Customer" && r.Property == "Country");
            // a report-level (extension) measure ⇒ flagged, not a model object
            Assert.Contains(f, r => r.Kind == "measure" && r.IsExtension && r.Property == "Report Margin");

            Assert.Equal(0, res.Unresolved);
        }

        [Fact]
        public void Path_aware_parse_stamps_page_visual_visualtype_and_keeps_fields_deduped()
        {
            // The SAME visual.json referenced by TWO visuals on one page (path → page/visual ids).
            var parts = new[]
            {
                ("definition/pages/PageA/visuals/v1/visual.json", VisualJson),
                ("definition/pages/PageA/visuals/v2/visual.json", VisualJson),
            };
            var res = ReportDefinitionReader.Parse(parts);

            // report-level Fields stays DEDUPED (page/visual excluded from Equals) — Total Sales appears once
            Assert.Equal(1, res.Fields.Count(r => r.Kind == "measure" && !r.IsExtension && r.Property == "Total Sales"));
            // Occurrences keep PER-VISUAL attribution — Total Sales stamped for BOTH v1 and v2
            var occ = res.Occurrences.Where(o => o.Property == "Total Sales" && !o.IsExtension).ToList();
            Assert.Contains(occ, o => o.Page == "PageA" && o.Visual == "v1");
            Assert.Contains(occ, o => o.Page == "PageA" && o.Visual == "v2");
            // visual type read from the visual.json
            Assert.Contains(res.Occurrences, o => o.VisualType == "barChart");
        }

        [Fact]
        public void Content_only_parse_leaves_page_visual_null_backcompat()
        {
            // The legacy content-only overload (no part paths) must still parse Fields AND leave page/visual null.
            var res = ReportDefinitionReader.Parse(new[] { VisualJson });
            Assert.Contains(res.Fields, r => r.Entity == "Sales" && r.Property == "Total Sales");
            Assert.All(res.Occurrences, o => Assert.Null(o.Page));
            Assert.All(res.Occurrences, o => Assert.Null(o.Visual));
        }

        [Fact]
        public void ReadLocalPbir_stamps_page_and_visual_from_the_folder_layout()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem_pbir_pv_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var def = Path.Combine(root, "Contoso.Report", "definition");
            var visualDir = Path.Combine(def, "pages", "p1", "visuals", "v1");
            try
            {
                Directory.CreateDirectory(visualDir);
                File.WriteAllText(Path.Combine(def, "report.json"), "{ \"$schema\": \"x\" }");
                File.WriteAllText(Path.Combine(visualDir, "visual.json"), VisualJson);

                var res = ReportDefinitionReader.ReadLocalPbir(root);
                Assert.Contains(res.Occurrences, o => o.Page == "p1" && o.Visual == "v1" && o.Entity == "Sales" && o.Property == "Total Sales");
            }
            finally { try { Directory.Delete(root, true); } catch { /* best-effort temp cleanup */ } }
        }

        [Fact]
        public void Unresolved_source_alias_is_counted_not_silently_dropped()
        {
            // A filter whose Source alias has NO matching From entry can't be attributed — it must bump Unresolved
            // (so the usage verdict degrades to caution) rather than vanish.
            const string orphanAlias = @"{ ""Where"": [ { ""Column"": { ""Expression"": { ""SourceRef"": { ""Source"": ""zz"" } }, ""Property"": ""Mystery"" } } ] }";
            var res = ReportDefinitionReader.Parse(new[] { orphanAlias });
            Assert.Empty(res.Fields);
            Assert.True(res.Unresolved >= 1);
        }

        [Fact]
        public void Malformed_part_is_skipped_not_thrown()
        {
            var res = ReportDefinitionReader.Parse(new[] { "{ not valid json ", VisualJson });
            Assert.NotEmpty(res.Fields);   // the good part still parsed
        }

        [Fact]
        public void ReadLocalPbir_resolves_a_pbip_report_folder_and_parses_it()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem_pbir_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var def = Path.Combine(root, "Contoso.Report", "definition");
            var visualDir = Path.Combine(def, "pages", "p1", "visuals", "v1");
            try
            {
                Directory.CreateDirectory(visualDir);
                File.WriteAllText(Path.Combine(def, "report.json"), "{ \"$schema\": \"x\" }");
                File.WriteAllText(Path.Combine(visualDir, "visual.json"), VisualJson);

                var res = ReportDefinitionReader.ReadLocalPbir(root);   // points at the PBIP project root
                Assert.Contains(res.Fields, r => r.Entity == "Sales" && r.Property == "Total Sales");
                Assert.Contains(res.Fields, r => r.Entity == "Customer" && r.Property == "Country");
            }
            finally { try { Directory.Delete(root, true); } catch { /* best-effort temp cleanup */ } }
        }
    }
}
