using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Sol join 6, round 2 (the class close). EVERY save from an editor or the Properties grid names the value the person started from, and the engine
    /// compares it INSIDE its own write: a value another writer put there since is refused (with the words the extension looks for), never written
    /// over, and a save from what is live, or with no starting point (the MCP door), writes as it always did. One test per setter that gained the fence;
    /// the web side of the same rule is test/save-class-expected-values.test.mjs, which finds every setter the webview and extension call.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Sol6SaveFenceTests : IDisposable
    {
        private const string Moved = "changed in the model after you opened it";
        private readonly string _root;
        private readonly string _safeRoot;

        public Sol6SaveFenceTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-fence-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static async Task<LocalEngine> FreshAsync()
        {
            var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.CreateModelAsync("Fence", 1606);
            await e.CreateTableAsync("T", "agent");
            return e;
        }

        /// <summary>A model with a home on disk, which the sidecar stores (a mapping, a saved check) need.</summary>
        private async Task<LocalEngine> SavedAsync()
        {
            var e = await FreshAsync();
            var dir = Path.Combine(_root, "model-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            await e.SaveAsync(dir, "TMDL", false, "human");
            return e;
        }

        private static async Task<string> Prop(LocalEngine e, string objRef, string name) =>
            (await e.GetObjectPropertiesAsync(objRef)).First(p => p.Name == name).Value;

        private static async Task Refused(Func<Task> write)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(write);
            Assert.Contains(Moved, ex.Message);
            Assert.Contains("Nothing was written", ex.Message);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Calculation_item_format_save_names_what_it_replaces()
        {
            using var e = await FreshAsync();
            var group = await e.CreateCalculationGroupAsync("Time", "agent");
            var item = await e.CreateCalculationItemAsync(group, "YoY %", "SELECTEDMEASURE()", "human");
            await e.SetCalcItemFormatStringAsync(item, "\"0.0%\"", "human");
            await e.SetCalcItemFormatStringAsync(item, "\"0.00 %\"", "agent");   // lands inside the page's 350 ms reload window

            await Refused(() => e.SetCalcItemFormatStringAsync(item, "\"0.0 %\"", "human", "\"0.0%\""));
            Assert.Equal("\"0.00 %\"", await Prop(e, item, "FormatStringExpression"));

            var ok = await e.SetCalcItemFormatStringAsync(item, "\"0.0 %\"", "human", "\"0.00 %\"");   // from what is live: writes
            Assert.True(ok.Changed);
            await e.SetCalcItemFormatStringAsync(item, "\"#,0\"", "human");   // no starting point: unfenced
            Assert.Equal("\"#,0\"", await Prop(e, item, "FormatStringExpression"));
            await e.SetCalcItemFormatStringAsync(item, "", "human", "\"#,0\"");   // clearing it names it too
            Assert.Equal("", await Prop(e, item, "FormatStringExpression"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Partition_and_shared_expression_M_saves_name_what_they_replace()
        {
            using var e = await FreshAsync();
            await e.CreateImportTableAsync("S", "let Source = 1 in Source", "human");
            const string part = "partition:S/S";
            await e.SetPartitionMAsync(part, "let Source = 2 in Source", "agent");

            await Refused(() => e.SetPartitionMAsync(part, "let Source = 3 in Source", "human", "let Source = 1 in Source"));
            Assert.Equal("let Source = 2 in Source", (await e.GetDocModelAsync(0)).Tables.First(t => t.Name == "S").Partitions[0].Source);
            Assert.True((await e.SetPartitionMAsync(part, "let Source = 3 in Source", "human", "let Source = 2 in Source")).Changed);

            await e.CreateNamedExpressionAsync("Param", "let Source = 1 in Source", "human");
            await e.UpdateNamedExpressionAsync("Param", "let Source = 2 in Source", "agent");
            await Refused(() => e.UpdateNamedExpressionAsync("Param", "let Source = 3 in Source", "human", "let Source = 1 in Source"));
            Assert.True((await e.UpdateNamedExpressionAsync("Param", "let Source = 3 in Source", "human", "let Source = 2 in Source")).Changed);
            await e.UpdateNamedExpressionAsync("Param", "let Source = 4 in Source", "human");   // unfenced
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Properties_grid_writes_name_the_value_the_grid_showed_for_one_object_and_for_a_batch()
        {
            using var e = await FreshAsync();
            var a = await e.CreateMeasureAsync("table:T", "A", "1", "agent");
            var b = await e.CreateMeasureAsync("table:T", "B", "2", "agent");
            await e.SetObjectPropertyAsync(a, "Description", "Old words.", "human");
            await e.SetObjectPropertyAsync(a, "Description", "The assistant's words.", "agent");

            await Refused(() => e.SetObjectPropertyAsync(a, "Description", "Typed words.", "human", "Old words."));
            Assert.Equal("The assistant's words.", await Prop(e, a, "Description"));
            Assert.True((await e.SetObjectPropertyAsync(a, "Description", "Typed words.", "human", "The assistant's words.")).Changed);
            // A property nobody set reads as empty, as the grid shows it.
            Assert.True((await e.SetObjectPropertyAsync(b, "DisplayFolder", "Folder", "human", "")).Changed);
            await e.SetObjectPropertyAsync(b, "DisplayFolder", "Other", "human");   // unfenced

            // A batch: every object must still hold the value the grid showed, or nothing is written.
            await e.SetObjectPropertyAsync(a, "IsHidden", "false", "human");
            await e.SetObjectPropertyAsync(b, "IsHidden", "false", "human");
            await e.SetObjectPropertyAsync(b, "IsHidden", "true", "agent");   // one of the two moved
            await Refused(() => e.SetObjectPropertiesAsync(new[] { a, b }, "IsHidden", "true", "human", "False"));
            Assert.Equal("False", await Prop(e, a, "IsHidden"));   // all or nothing: the first one was not written either
            await e.SetObjectPropertyAsync(b, "IsHidden", "false", "agent");
            Assert.True((await e.SetObjectPropertiesAsync(new[] { a, b }, "IsHidden", "true", "human", "False")).Changed);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Measure_format_expression_and_column_data_type_saves_name_what_they_replace()
        {
            using var e = await FreshAsync();
            var m = await e.CreateMeasureAsync("table:T", "M", "1", "agent");
            await e.SetMeasureFormatExpressionAsync(m, "\"0.0\"", "human");
            await e.SetMeasureFormatExpressionAsync(m, "\"0.00\"", "agent");
            await Refused(() => e.SetMeasureFormatExpressionAsync(m, "\"0\"", "human", "\"0.0\""));
            Assert.Equal("\"0.00\"", await Prop(e, m, "FormatStringExpression"));
            Assert.True((await e.SetMeasureFormatExpressionAsync(m, "\"0\"", "human", "\"0.00\"")).Changed);

            var col = await e.CreateColumnAsync("table:T", "K", "int64", null, "human");
            await e.SetColumnDataTypeAsync(col, "string", "agent");
            await Refused(() => e.SetColumnDataTypeAsync(col, "double", "human", "Int64"));
            Assert.Equal("String", await Prop(e, col, "DataType"));
            Assert.True((await e.SetColumnDataTypeAsync(col, "double", "human", "String")).Changed);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Plan_item_value_and_calculation_group_precedence_saves_name_what_they_replace()
        {
            using var e = await FreshAsync();
            var m = await e.CreateMeasureAsync("table:T", "M", "1", "agent");
            var plan = await e.AddPlanItemAsync(m, "set_description", "first words", null, null, null, "agent");
            var id = plan.Items.Single().Id;
            await e.SetPlanItemAsync(id, "the assistant's words", null, "agent");

            await Refused(() => e.SetPlanItemAsync(id, "typed words", null, "human", "first words"));
            Assert.Equal("the assistant's words", (await e.GetPlanAsync()).Items.Single().After);
            await e.SetPlanItemAsync(id, "typed words", null, "human", "the assistant's words");
            Assert.Equal("typed words", (await e.GetPlanAsync()).Items.Single().After);
            await e.SetPlanItemAsync(id, null, true, "human", "never compared: a tick has no new value");   // a tick passes no value, so nothing is compared
            await e.SetPlanItemAsync(id, "unfenced words", null, "human");
            Assert.Equal("unfenced words", (await e.GetPlanAsync()).Items.Single().After);

            var group = await e.CreateCalculationGroupAsync("Time", "agent");
            await e.SetCalcGroupPrecedenceAsync(group, 5, "agent");
            await Refused(() => e.SetCalcGroupPrecedenceAsync(group, 9, "human", "0"));
            Assert.Equal("5", await Prop(e, group, "CalculationGroupPrecedence"));
            Assert.True((await e.SetCalcGroupPrecedenceAsync(group, 9, "human", "5")).Changed);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Ai_instructions_save_names_what_it_replaces()
        {
            using var e = await FreshAsync();
            await e.SetAiInstructionsAsync("First words.", null, "human");
            await e.SetAiInstructionsAsync("The assistant's words.", null, "agent");

            await Refused(() => e.SetAiInstructionsAsync("Typed words.", null, "human", "First words."));
            Assert.Equal("The assistant's words.", (await e.GetAiInstructionsAsync(null)).Instructions);
            Assert.True((await e.SetAiInstructionsAsync("Typed words.", null, "human", "The assistant's words.")).Changed);
            await e.SetAiInstructionsAsync("Unfenced words.", null, "human");
            Assert.Equal("Unfenced words.", (await e.GetAiInstructionsAsync(null)).Instructions);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Spec_save_names_the_version_it_started_from()
        {
            using var e = await FreshAsync();
            var first = await e.SetSpecAsync("{\"name\":\"One\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "human");
            var second = await e.SetSpecAsync("{\"name\":\"Two\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "agent");

            await Refused(() => e.SetSpecAsync("{\"name\":\"Mine\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "human", first.Version));
            Assert.Equal("Two", (await e.GetSpecAsync()).Spec.Name);
            await e.SetSpecAsync("{\"name\":\"Mine\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "human", second.Version);
            Assert.Equal("Mine", (await e.GetSpecAsync()).Spec.Name);
            await e.SetSpecAsync("{\"name\":\"Unfenced\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}", "human");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Refresh_policy_save_names_the_policy_it_started_from()
        {
            using var e = await FreshAsync();
            const string m = "let\n    Source = #table(type table [order_dt = datetime], {})\nin\n    Source";
            foreach (var t in new[] { "IR", "IR2" })
            {
                await e.CreateImportTableAsync(t, m, "agent");
                await e.CreateColumnAsync("table:" + t, "OrderDate", "DateTime", "order_dt", "agent");
            }
            string Policy(object body) => System.Text.Json.JsonSerializer.Serialize(body);
            await e.SetIncrementalRefreshPolicyAsync("table:IR", "OrderDate", 5, "Year", 10, "Day", 0, "Import", null, true, "human");
            var started = Policy(new { enabled = true, rollingWindowPeriods = 5, rollingWindowGranularity = "Year", incrementalPeriods = 10, incrementalGranularity = "Day", mode = "Import" });
            await e.SetIncrementalRefreshPolicyAsync("table:IR", "OrderDate", 7, "Year", 10, "Day", 0, "Import", null, true, "agent");   // the assistant widened the store window

            await Refused(() => e.SetIncrementalRefreshPolicyAsync("table:IR", "OrderDate", 5, "Year", 20, "Day", 0, "Import", null, true, "human", started));
            Assert.Equal(7, (await e.GetIncrementalRefreshPolicyAsync("table:IR")).RollingWindowPeriods);
            var live = Policy(new { enabled = true, rollingWindowPeriods = 7, rollingWindowGranularity = "Year", incrementalPeriods = 10, incrementalGranularity = "Day", mode = "Import" });
            Assert.True((await e.SetIncrementalRefreshPolicyAsync("table:IR", "OrderDate", 7, "Year", 20, "Day", 0, "Import", null, true, "human", live)).Changed);
            // A table with no policy: the page read it as off, so the starting point is "not enabled", and a policy the assistant added since is refused.
            var off = Policy(new { enabled = false });
            await e.SetIncrementalRefreshPolicyAsync("table:IR2", "OrderDate", 3, "Year", 5, "Day", 0, "Import", null, true, "agent");
            await Refused(() => e.SetIncrementalRefreshPolicyAsync("table:IR2", "OrderDate", 9, "Year", 5, "Day", 0, "Import", null, true, "human", off));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Custom_rule_edit_names_the_rule_it_started_from()
        {
            using var e = await FreshAsync();
            const string rule = "[{\"ID\":\"R1\",\"Name\":\"Rule\",\"Category\":\"Maintenance\",\"Severity\":2,\"Scope\":\"Measure\",\"Expression\":\"not IsHidden\"}]";
            await e.LoadBpaRulesAsync(rule, true, "human");
            var started = (await e.GetCustomRulesAsync()).Bpa.Single();
            var startedJson = Newtonsoft.Json.JsonConvert.SerializeObject(started, new Newtonsoft.Json.JsonSerializerSettings
            { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(), NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
            await e.LoadBpaRulesAsync(rule.Replace("not IsHidden", "IsHidden"), false, "agent");   // the assistant rewrote the same rule

            await Refused(() => e.LoadBpaRulesAsync(rule.Replace("Name\":\"Rule", "Name\":\"Mine"), false, "human", startedJson));
            Assert.Equal("Rule", (await e.GetCustomRulesAsync()).Bpa.Single().Name);
            var liveJson = Newtonsoft.Json.JsonConvert.SerializeObject((await e.GetCustomRulesAsync()).Bpa.Single(), new Newtonsoft.Json.JsonSerializerSettings
            { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(), NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore });
            await e.LoadBpaRulesAsync(rule.Replace("Name\":\"Rule", "Name\":\"Mine"), false, "human", liveJson);
            Assert.Equal("Mine", (await e.GetCustomRulesAsync()).Bpa.Single().Name);
            // A rule that was removed since the editor opened is not recreated by an edit.
            await e.ResetBpaRulesAsync("agent");
            await Refused(() => e.LoadBpaRulesAsync(rule, false, "human", liveJson));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Sql_source_save_names_the_record_it_started_from()
        {
            using var e = await FreshAsync();
            var saved = await e.SaveSqlSourceAsync(null, "Warehouse", "contoso-sql.example", "Warehouse", "interactive", null, "human");
            await e.SaveSqlSourceAsync(saved.Id, "Warehouse", "other-sql.example", "Warehouse", "interactive", null, "agent");   // the assistant re-pointed it
            var started = "{\"name\":\"Warehouse\",\"server\":\"contoso-sql.example\",\"database\":\"Warehouse\",\"authMode\":\"interactive\",\"tenantId\":null}";

            await Refused(() => e.SaveSqlSourceAsync(saved.Id, "Warehouse", "contoso-sql.example", "Mine", "interactive", null, "human", started));
            Assert.Equal("other-sql.example", SqlSourceRegistry.Find(saved.Id).Server);
            var live = "{\"name\":\"Warehouse\",\"server\":\"other-sql.example\",\"database\":\"Warehouse\",\"authMode\":\"interactive\",\"tenantId\":null}";
            await e.SaveSqlSourceAsync(saved.Id, "Warehouse", "other-sql.example", "Mine", "interactive", null, "human", live);
            Assert.Equal("Mine", SqlSourceRegistry.Find(saved.Id).Database);
            await e.SaveSqlSourceAsync(saved.Id, "Warehouse", "other-sql.example", "Unfenced", "interactive", null, "human");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Table_mapping_and_saved_check_saves_name_what_they_replace()
        {
            using var e = await SavedAsync();
            var sales = await McpTools.CreateTable(e, "Sales");
            await McpTools.CreateMeasure(e, sales, "Total Sales", "4321");
            var one = await e.SaveSqlSourceAsync(null, "One", "contoso-sql.example", "W1", "interactive", null, "human");
            var two = await e.SaveSqlSourceAsync(null, "Two", "contoso-sql.example", "W2", "interactive", null, "human");
            await e.SetTableSourceMappingAsync("Sales", one.Id, "dbo", "orders", "human");
            await e.SetTableSourceMappingAsync("Sales", two.Id, "dbo", "orders", "agent");   // the assistant re-mapped it
            var started = "{\"sqlSourceId\":\"" + one.Id + "\",\"schema\":\"dbo\",\"entity\":\"orders\"}";

            await Refused(() => e.SetTableSourceMappingAsync("Sales", one.Id, "dbo", "lines", "human", started));
            Assert.Equal(two.Id, (await e.ListTableSourceMappingsAsync()).Rows.Single(r => r.ModelTable == "Sales").SqlSourceId);
            var live = "{\"sqlSourceId\":\"" + two.Id + "\",\"schema\":\"dbo\",\"entity\":\"orders\"}";
            await e.SetTableSourceMappingAsync("Sales", two.Id, "dbo", "lines", "human", live);
            // A table with no stored mapping starts empty; a mapping the assistant stored since is refused.
            await e.CreateTableAsync("Dim", "agent");
            await e.SetTableSourceMappingAsync("Dim", one.Id, "dbo", "dim", "agent");
            await Refused(() => e.SetTableSourceMappingAsync("Dim", two.Id, "dbo", "dim", "human", "{\"sqlSourceId\":\"\",\"schema\":\"\",\"entity\":\"\"}"));

            var def = new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = "revenue vs source", TargetRef = "measure:Sales/Total Sales", Enabled = true,
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest { MeasureRef = "measure:Sales/Total Sales", Sql = "SELECT 4321 AS total", SqlSourceId = one.Id, BlankPolicy = "zero" }),
            };
            var stored = await e.SaveTestDefinitionAsync(def, "human");
            var theirs = (await e.ListTestDefinitionsAsync()).Definitions.Single(d => d.Id == stored.Id);
            theirs.Title = "The assistant's title";
            await e.SaveTestDefinitionAsync(theirs, "agent");
            var mine = (await e.ListTestDefinitionsAsync()).Definitions.Single(d => d.Id == stored.Id);
            mine.Title = "Typed title";
            var startedCheck = "{\"title\":\"revenue vs source\",\"kind\":\"" + TestKinds.MeasureReconcile + "\",\"targetRef\":\"measure:Sales/Total Sales\",\"enabled\":true}";

            await Refused(() => e.SaveTestDefinitionAsync(mine, "human", startedCheck));
            Assert.Equal("The assistant's title", (await e.ListTestDefinitionsAsync()).Definitions.Single(d => d.Id == stored.Id).Title);
            var liveCheck = "{\"title\":\"The assistant's title\",\"kind\":\"" + TestKinds.MeasureReconcile + "\",\"targetRef\":\"measure:Sales/Total Sales\",\"enabled\":true}";
            await e.SaveTestDefinitionAsync(mine, "human", liveCheck);
            Assert.Equal("Typed title", (await e.ListTestDefinitionsAsync()).Definitions.Single(d => d.Id == stored.Id).Title);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Primer_save_names_the_notes_it_replaces()
        {
            var dir = Path.Combine(_root, "primer");
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.OpenAsync(model);
            var template = (await e.GetPrimerAsync()).Markdown;
            var first = template.Replace("_Add what people and the AI Assistant should know._", "First words.");
            await e.SetPrimerAsync(first, "human", template);   // from what is live (the starter text): writes
            var theirs = template.Replace("_Add what people and the AI Assistant should know._", "The assistant's words.");
            await e.SetPrimerAsync(theirs, "agent");

            var mine = template.Replace("_Add what people and the AI Assistant should know._", "Typed words.");
            await Refused(() => e.SetPrimerAsync(mine, "human", first));
            Assert.Contains("The assistant's words.", (await e.GetPrimerAsync()).Markdown);
            await e.SetPrimerAsync(mine, "human", (await e.GetPrimerAsync()).Markdown);
            Assert.Contains("Typed words.", (await e.GetPrimerAsync()).Markdown);
        }

        [Fact]
        public async Task Lesson_edit_names_the_lesson_it_replaces()
        {
            var ws = Path.Combine(_root, "ws");
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus"));
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro, ws);
            var added = await e.AddInsightAsync("Use DIVIDE", new[] { "dax" }, "insight", "project", false, "agent");
            await e.EditInsightAsync(added.Id, "Use DIVIDE not slash", new[] { "dax" }, "agent");   // the assistant reworded it
            var started = "{\"text\":\"Use DIVIDE\",\"keys\":[\"dax\"]}";

            await Refused(() => e.EditInsightAsync(added.Id, "Typed words", new[] { "dax" }, "human", started));
            Assert.Equal("Use DIVIDE not slash", (await e.ListInsightsAsync("project", null)).Insights.Single(i => i.Id == added.Id).Text);
            var live = "{\"text\":\"Use DIVIDE not slash\",\"keys\":[\"dax\"]}";
            await e.EditInsightAsync(added.Id, "Typed words", new[] { "dax" }, "human", live);
            Assert.Equal("Typed words", (await e.ListInsightsAsync("project", null)).Insights.Single(i => i.Id == added.Id).Text);
        }
    }
}
