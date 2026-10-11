using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat-undo, item U5 (Sol UAT-F-S04c-007, J8-08): applying a TMDL script must keep every object's words.
    /// A table's or role's description is a "///" line ABOVE its column-0 declaration, and the script splitter filed
    /// that line with the document before it (or dropped it when it came first), so a valid script erased the table
    /// descriptions it carried. Class: a doc comment that sits outside the declaration it describes. The columns,
    /// measures, hierarchies and partitions inside a table document are indented and stay in it; they are held here too.
    /// </summary>
    public sealed class UatTmdlDocCommentTests
    {
        private static async Task<(LocalEngine engine, SessionManager sessions)> NewAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("TmdlWords", 1604);
            await engine.CreateImportTableAsync("Sales", "let Source = 1 in Source", "human");
            await engine.CreateColumnAsync("table:Sales", "Region", "String", "Region", "human");
            await engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await engine.CreateMeasureAsync("table:Sales", "Total", "SUM(Sales[Amount])", "human");
            await engine.CreateHierarchyAsync("table:Sales", "Geo", new[] { "Region" }, "human");
            await engine.CreateImportTableAsync("Clients", "let Source = 2 in Source", "human");
            await engine.CreateColumnAsync("table:Clients", "Name", "String", "Name", "human");
            await engine.CreateRoleAsync("Managers", "Read", "human");
            await engine.SetDescriptionAsync("table:Sales", "One row per sale.", "human");
            await engine.SetDescriptionAsync("table:Clients", "Who buys.", "human");
            await engine.SetDescriptionAsync("column:Sales/Region", "Where it sold.", "human");
            await engine.SetDescriptionAsync("measure:Sales/Total", "All sales added up.", "human");
            await engine.SetDescriptionAsync("hierarchy:Sales/Geo", "Region drill.", "human");
            await engine.SetDescriptionAsync("role:Managers", "Regional managers.", "human");
            await engine.SetDescriptionAsync("partition:Sales/Sales", "Main load.", "human");
            return (engine, sessions);
        }

        private static async Task<string[]> WordsAsync(SessionManager sessions) =>
            await sessions.Current.ReadAsync(m => new[]
            {
                m.Tables["Sales"].Description, m.Tables["Clients"].Description, m.Tables["Sales"].Columns["Region"].Description,
                m.Tables["Sales"].Measures["Total"].Description, m.Tables["Sales"].Hierarchies["Geo"].Description,
                m.Roles["Managers"].Description, m.Tables["Sales"].Partitions["Sales"].Description,
            });

        private static readonly string[] Expected =
            { "One row per sale.", "Who buys.", "Where it sold.", "All sales added up.", "Region drill.", "Regional managers.", "Main load." };

        [Fact]
        public async Task A_scripted_and_applied_back_tmdl_keeps_every_description_with_no_edit()
        {
            var (engine, sessions) = await NewAsync();
            using (engine)
            {
                var script = await engine.ScriptObjectsAsync(new[] { "table:Sales", "table:Clients", "role:Managers" }, "tmdl");
                Assert.Contains("/// One row per sale.", script);
                var result = await engine.ApplyTmdlScriptAsync(script, "human");
                Assert.Empty(result.Skipped);
                Assert.Equal(Expected, await WordsAsync(sessions));
            }
        }

        /// <summary>The script without its "// @object ..." header line, base marker and all (a header now carries "@base:<hash>"; the hand-made form has none).</summary>
        private static string NoHeader(string script) =>
            System.Text.RegularExpressions.Regex.Replace(script, @"^//\s*@object[^\n]*\n?", "", System.Text.RegularExpressions.RegexOptions.Multiline).Trim();

        [Fact]
        public async Task Several_top_level_documents_in_one_script_each_keep_their_own_doc_comment()
        {
            var (engine, sessions) = await NewAsync();
            using (engine)
            {
                // The hand-made form with no object headers: a doc comment belongs to the declaration BELOW it.
                var one = NoHeader(await engine.ScriptObjectsAsync(new[] { "table:Sales" }, "tmdl"));
                var two = NoHeader(await engine.ScriptObjectsAsync(new[] { "table:Clients" }, "tmdl"));
                var role = NoHeader(await engine.ScriptObjectsAsync(new[] { "role:Managers" }, "tmdl"));
                var script = one.Replace("/// One row per sale.", "/// Sales, edited.") + "\n\n" + two.Replace("/// Who buys.", "/// Clients, edited.")
                           + "\n\n" + role.Replace("/// Regional managers.", "/// Managers, edited.") + "\n";
                var result = await engine.ApplyTmdlScriptAsync(script, "human");
                Assert.Empty(result.Skipped);
                var words = await WordsAsync(sessions);
                Assert.Equal("Sales, edited.", words[0]);
                Assert.Equal("Clients, edited.", words[1]);
                Assert.Equal("Managers, edited.", words[5]);
                Assert.Equal(Expected.Skip(2).Take(3).Concat(new[] { "Main load." }), words.Skip(2).Take(3).Concat(new[] { words[6] }));
            }
        }

        [Fact]
        public async Task A_doc_comment_added_above_a_table_that_had_none_is_applied()
        {
            var (engine, sessions) = await NewAsync();
            using (engine)
            {
                await engine.CreateImportTableAsync("Stores", "let Source = 3 in Source", "human");
                await engine.CreateColumnAsync("table:Stores", "City", "String", "City", "human");
                var script = await engine.ScriptObjectsAsync(new[] { "table:Stores" }, "tmdl");
                Assert.DoesNotContain("///", script.Split('\n').First(l => l.StartsWith("table ", StringComparison.Ordinal)));
                script = script.Replace("table Stores", "/// Where we sell.\ntable Stores");
                var result = await engine.ApplyTmdlScriptAsync(script, "human");
                Assert.Empty(result.Skipped);
                Assert.Equal("Where we sell.", await sessions.Current.ReadAsync(m => m.Tables["Stores"].Description));
            }
        }

        [Fact]
        public async Task An_edited_description_inside_a_table_document_is_applied_and_the_rest_survive()
        {
            var (engine, sessions) = await NewAsync();
            using (engine)
            {
                var script = (await engine.ScriptObjectsAsync(new[] { "table:Sales" }, "tmdl"))
                    .Replace("/// Where it sold.", "/// Sales region.").Replace("/// All sales added up.", "/// Sum of Amount.")
                    .Replace("/// Region drill.", "/// Drill by region.");
                var result = await engine.ApplyTmdlScriptAsync(script, "human");
                Assert.Empty(result.Skipped);
                Assert.Equal(new[] { "One row per sale.", "Who buys.", "Sales region.", "Sum of Amount.", "Drill by region.", "Regional managers.", "Main load." },
                    await WordsAsync(sessions));
            }
        }
    }
}
