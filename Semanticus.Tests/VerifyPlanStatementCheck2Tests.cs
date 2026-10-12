using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Plan statement check 2 (verifier, 1 Oct, on m11/join 90716ced). Two defects met on the from-scratch journey, each
    /// reproduced on the real app (UAT lane c and the MCP door) before it was written down here. Left failing on purpose.
    /// </summary>
    public sealed class VerifyPlanStatementCheck2Tests
    {
        private const string Why = "Kept off on purpose; a measure uses it.";

        private static string Scratch()
        {
            var dir = Path.Combine(Path.GetTempPath(), "psc2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>The Plan's "Save the changes you have made" on a new model offers "Save as a folder" with the workspace folder
        /// as the default. When that folder already holds a Power BI project, the save resolves to that project's
        /// definition/ folder and replaces its model: in the lane, Northstar went from 15 files to the new model's 3, its
        /// perspectives, roles and cultures deleted, with no question asked. A first save must never replace another model.</summary>
        [Fact]
        public async Task A_new_models_first_save_into_a_folder_holding_another_project_never_replaces_that_projects_model()
        {
            var root = Scratch();
            try
            {
                var def = Path.Combine(root, "Existing.SemanticModel", "definition");
                Directory.CreateDirectory(Path.Combine(def, "tables"));
                File.WriteAllText(Path.Combine(root, "Existing.pbip"), "{}");
                File.WriteAllText(Path.Combine(def, "database.tmdl"), "database Existing\n\tcompatibilityLevel: 1604\n");
                File.WriteAllText(Path.Combine(def, "model.tmdl"), "model Model\n\tculture: en-US\n");
                var keep = Path.Combine(def, "tables", "Keep.tmdl");
                File.WriteAllText(keep, "table Keep\n\n\tcolumn Id\n\t\tdataType: int64\n\t\tsourceColumn: Id\n");
                var before = File.ReadAllText(keep);

                using var f = new DecisionFixture(TestEntitlements.Pro);
                await f.Engine.CreateModelAsync("Brand New", 1604);
                await f.Engine.CreateTableAsync("Only", "human");

                try { await f.Engine.SaveAsync(root, "TMDL", false, "human"); }
                catch (Exception) { /* a refusal is a right answer; what must never happen is the other model being replaced */ }

                Assert.True(File.Exists(keep), "the other project's table file was deleted by the new model's first save");
                Assert.Equal(before, File.ReadAllText(keep));
                Assert.False(File.Exists(Path.Combine(def, "tables", "Only.tmdl")), "the new model was written into the other project's definition folder");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>The assistant renames tables (dim_project to Project) as the words row asks. Acceptances on tables and columns
        /// follow the rename; an acceptance on a RELATIONSHIP keeps the old table names, so the finding opens again, the Plan's
        /// "Sort out N relationships" row comes back, and the scan does not count the stale record as orphaned either.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_acceptance_on_a_relationship_follows_a_rename_of_a_table_it_joins()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Psc2", 1604);
            await e.CreateTableAsync("fact_invoice", "human");
            await e.CreateColumnAsync("table:fact_invoice", "INV_DT", "DateTime", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "DUE_DT", "DateTime", null, "human");
            await e.CreateTableAsync("dim_date", "human");
            await e.CreateColumnAsync("table:dim_date", "CAL_DT", "DateTime", null, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/INV_DT", "column:dim_date/CAL_DT", null, true, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/DUE_DT", "column:dim_date/CAL_DT", null, false, "human");

            var beforeRef = (await e.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "REL-INACTIVE").ObjectRef;
            await e.WaiveFindingAsync("air", "REL-INACTIVE", beforeRef, Why, "human");
            Assert.True((await e.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "REL-INACTIVE").Waived);

            await new EngineRpcTarget(e).renameObject("table:fact_invoice", "Invoices");
            await new EngineRpcTarget(e).renameObject("table:dim_date", "Date");

            var scan = await e.AiReadinessScanAsync();
            var after = scan.Findings.Single(x => x.RuleId == "REL-INACTIVE");
            Assert.Contains("'Invoices'", after.ObjectRef);
            Assert.True(after.Waived || scan.OrphanedWaivers.Any(),
                "the acceptance was lost on rename and is not reported as orphaned either: " + after.ObjectRef);
            Assert.True(after.Waived, "the acceptance did not follow the table rename: " + after.ObjectRef);
        }
    }
}
