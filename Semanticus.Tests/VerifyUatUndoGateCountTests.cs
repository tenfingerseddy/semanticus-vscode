using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, lane m11/uat-undo redesign (06ac25f8). Kane: guardrails must not hurt UX. A dozen common assistant edits, made
    /// through the assistant's door in each preset, counting the questions the person is asked. Self-contained (no gate types named),
    /// so the same file runs on the base (de4f169d, the assistant's door is the engine itself) and on the redesign (the gated engine).
    /// Measured on both: the counts below are the base's, and the redesign must not ask more below Locked.
    /// </summary>
    [Collection("restore-root")]
    public sealed class VerifyUatUndoGateCountTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _oldHome;

        public VerifyUatUndoGateCountTests(RestoreRootFixture fixture, ITestOutputHelper output)
        {
            _out = output;
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "vgc-" + Guid.NewGuid().ToString("N").Substring(0, 5));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            _oldHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _oldHome);
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static IEngine AssistantDoor(LocalEngine engine)
        {
            var gate = typeof(LocalEngine).Assembly.GetType("Semanticus.Engine.AssistantEngine");
            if (gate == null) return engine;
            var make = gate.GetMethod("For", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(LocalEngine) }, null);
            return (IEngine)make.Invoke(null, new object[] { engine });
        }

        private string WriteModel(string tag)
        {
            var dir = Path.Combine(_root, tag);
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("Count") { CompatibilityLevel = 1604, Model = new TOM.Model() };
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales" };
            sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region", LineageTag = "c-re" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Code", DataType = TOM.DataType.String, SourceColumn = "Code", LineageTag = "c-co" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am" });
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", LineageTag = "m-total" });
            sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "1", LineageTag = "m-margin" });
            sales.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(sales);
            var path = Path.Combine(dir, "model.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        private async Task<(int asked, int refused)> RunPresetAsync(string preset)
        {
            var path = WriteModel("p-" + preset);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.OpenAsync(path);
            // The person's own work that lives in the session's memory: a spec in the Spec tab and a plan line they approved. Replacing or clearing
            // it is held under Locked (join 6, Sol item 7) and must stay a question below Locked: none.
            await engine.SetSpecAsync("{\"name\":\"S\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "human");
            var line = (await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "reviewed", null, null, null, "human")).Items[0].Id;
            await engine.SetPlanItemAsync(line, null, true, "human");
            AgentPolicyStore.SetPreset(preset, "human");
            var door = AssistantDoor(engine);
            var edits = new List<(string, Func<Task>)>
            {
                ("set_description", () => McpTools.SetDescription(door, "measure:Sales/Total", "words")),
                ("update_measure", () => McpTools.UpdateMeasure(door, "measure:Sales/Margin", "2")),
                ("set_measure_format", () => McpTools.SetMeasureFormat(door, "measure:Sales/Total", "0.0")),
                ("set_display_folder", () => McpTools.SetDisplayFolder(door, new[] { "measure:Sales/Total" }, "Folder")),
                ("set_column_hidden", () => McpTools.SetColumnHidden(door, "column:Sales/Code", true)),
                ("set_synonyms", () => McpTools.SetSynonyms(door, "column:Sales/Region", new[] { "area" })),
                ("create_measure", () => McpTools.CreateMeasure(door, "table:Sales", "Extra", "1")),
                ("create_calculated_column", () => McpTools.CreateCalculatedColumn(door, "table:Sales", "Calc", "1")),
                ("rename_object", () => McpTools.RenameObject(door, "measure:Sales/Margin", "Margin Two")),
                ("set_property", () => McpTools.SetProperty(door, "measure:Sales/Total", "IsHidden", "true")),
                ("delete_object", () => McpTools.DeleteObject(door, "measure:Sales/Extra")),
                ("undo_change", () => McpTools.UndoChange(door)),
                ("save_model", () => McpTools.SaveModel(door)),
                ("export_vpax", () => McpTools.ExportVpax(door, Path.Combine(_root, "p-" + preset, "out.vpax"))),
                ("set_spec", () => McpTools.SetSpec(door, "{\"name\":\"Other\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}")),
                ("clear_spec", () => McpTools.ClearSpec(door)),
                ("clear_plan", () => McpTools.ClearPlan(door)),
            };
            int asked = 0, refused = 0;
            foreach (var (name, edit) in edits)
            {
                var before = (await engine.ListPendingApprovalsAsync()).Length;
                var failed = false;
                try { await edit(); } catch (Exception) { failed = true; }
                var after = (await engine.ListPendingApprovalsAsync()).Length;
                if (after > before) asked++;
                if (failed) refused++;
                _out.WriteLine($"{preset,-9} {name,-26} asked={(after > before ? 1 : 0)} failed={(failed ? 1 : 0)}");
                foreach (var p in await engine.ListPendingApprovalsAsync()) await engine.DenyAgentActionAsync(p.Id, "human");
            }
            _out.WriteLine($"{preset,-9} TOTAL asked={asked} failed={refused}");
            return (asked, refused);
        }

        /// <summary>Lean suite: the default tier makes the dozen edits in the strictest preset below Locked ("client"), which asks nothing.
        /// All five presets (and the Locked count, which is only reported) are the sweep tier.</summary>
        [Fact]
        public async Task Below_Locked_a_dozen_common_assistant_edits_ask_no_more_than_on_the_base()
        {
            var counts = await RunPresetAsync("client");
            Assert.True(counts.asked == 0, "client asked " + counts.asked + " questions");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Below_Locked_a_dozen_common_assistant_edits_ask_no_more_than_on_the_base_in_every_preset()
        {
            var counts = new Dictionary<string, (int asked, int refused)>();
            foreach (var preset in new[] { "open", "standard", "cautious", "client", "locked" })
                counts[preset] = await RunPresetAsync(preset);
            foreach (var preset in new[] { "open", "standard", "cautious", "client" })
                Assert.True(counts[preset].asked == 0, preset + " asked " + counts[preset].asked + " questions");
        }
    }
}
