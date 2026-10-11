// Proof for the lean suite (lane 0): opening a saved copy of the AskData rig's model is the same model as building it.
// AskDataTests builds its Contoso Retail model with about thirty engine calls per test. The class lanes replace that with one
// build and a copy per test (ModelTemplates), which is only sound if the copy IS the model the build would have made.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        // A key with a calendar and a ratio measure, so the proof covers the optional parts of the build too. It builds the rig and the
        // template (two full builds, about ten seconds on a loaded machine), so it is a sweep test: the nightly run proves the copy
        // is the model, and the default run does not pay for it. The cheap template tests are in TestTemplatesTests.
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Template_open_gives_the_same_model_as_the_AskData_build()
        {
            var buildClock = Stopwatch.StartNew();
            using var built = await BuildAsync(connect: false, calendar: true, ratio: true);
            var buildMs = buildClock.ElapsedMilliseconds;
            using var templates = new ModelTemplates();
            Task BuildTemplate(DecisionFixture f) => BuildContosoModelAsync(f.Engine, calendar: true, ratio: true);
            using (var first = new DecisionFixture(TestEntitlements.Pro))
                await templates.OpenCopyAsync(first, "contoso-calendar-ratio", BuildTemplate, "ContosoRetail.bim");   // pays the one build
            using var opened = new DecisionFixture(TestEntitlements.Pro);
            var openClock = Stopwatch.StartNew();
            await templates.OpenCopyAsync(opened, "contoso-calendar-ratio", BuildTemplate, "ContosoRetail.bim");
            var openMs = openClock.ElapsedMilliseconds;
            Assert.Equal(1, templates.Builds);
            // An open is one file read; the build is about thirty writes. If the open is not cheaper, a template buys nothing.
            Assert.True(openMs < buildMs, $"opening the template took {openMs} ms and building the model took {buildMs} ms");

            // The same model as the engine sees it: tables, columns, measures, expressions, formats, relationships.
            static Task<string> ShapeAsync(DecisionFixture f) => f.Sessions.Require().ReadAsync(m =>
            {
                var lines = new List<string>();
                foreach (var t in m.Tables)
                {
                    foreach (var c in t.Columns) lines.Add("column " + t.Name + "[" + c.Name + "] " + c.DataType);
                    foreach (var x in t.Measures) lines.Add("measure " + t.Name + "[" + x.Name + "] " + x.Expression + " | " + x.FormatString + " | " + x.Description);
                }
                foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
                    lines.Add("relationship " + r.FromColumn.Table.Name + "[" + r.FromColumn.Name + "] -> " + r.ToColumn.Table.Name + "["
                        + r.ToColumn.Name + "] active=" + r.IsActive + " " + r.CrossFilteringBehavior);
                lines.Sort(StringComparer.Ordinal);
                return string.Join("\n", lines);
            });
            Assert.Equal(await ShapeAsync(built.F), await ShapeAsync(opened));

            // And the same file: saving the opened copy writes what the build wrote. Every build draws fresh GUIDs (lineage tags and
            // relationship names), so those, and only those, are blanked before the two files are compared.
            static string Plain(string bim) => System.Text.RegularExpressions.Regex.Replace(bim,
                "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<guid>");
            await opened.SaveAsAsync("Again.bim");
            Assert.Equal(Plain(File.ReadAllText(Path.Combine(built.F.Workspace, "ContosoRetail.bim"))),
                Plain(File.ReadAllText(Path.Combine(opened.Workspace, "Again.bim"))));
        }
    }
}
