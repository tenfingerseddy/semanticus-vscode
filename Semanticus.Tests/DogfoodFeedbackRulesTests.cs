using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Fixes from a dogfooding pass that drove the engine over MCP on a real star-schema model (27 Sep). Each test
    /// reproduces one reported defect on a small Northstar model and was watched failing before its fix. Most go
    /// through the MCP tool methods themselves, so what is asserted is what an assistant is actually served.
    /// </summary>
    public sealed class DogfoodFeedbackRulesTests : IClassFixture<ModelTemplates>
    {
        // Lean suite (8 Oct): the Northstar model is built once per class (ModelTemplates) and every test opens its own copy.
        // Corpora of format strings and DAX shapes run on ONE model, a measure per case, instead of one model per case: the same
        // findings, the same presses and the same reads, without paying the build and the scan again for every row.
        private readonly ModelTemplates _templates;
        public DogfoodFeedbackRulesTests(ModelTemplates templates) => _templates = templates;

        // ---- helpers ------------------------------------------------------------------------------------------

        private static MethodInfo[] ToolMethods() =>
            typeof(McpTools).Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed && t.Name.StartsWith("McpTools", StringComparison.Ordinal))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(m => ToolName(m) != null)
                .ToArray();

        private static string ToolName(MethodInfo m) =>
            m.GetCustomAttributes(true).Where(a => a.GetType().Name == "McpServerToolAttribute")
                .Select(a => (string)a.GetType().GetProperty("Name")?.GetValue(a)).FirstOrDefault();

        /// <summary>Call an MCP tool method by its wire name with named arguments; unnamed ones take their defaults.</summary>
        private static async Task<JsonElement> CallToolAsync(IEngine engine, string tool, object args = null)
        {
            var m = ToolMethods().Single(x => ToolName(x) == tool);
            var given = args == null
                ? new Dictionary<string, object>()
                : args.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(args));
            var unknown = given.Keys.Where(k => m.GetParameters().All(p => p.Name != k)).ToArray();
            Assert.True(unknown.Length == 0, $"{tool} has no parameter named {string.Join(", ", unknown)}");
            var values = m.GetParameters().Select(p =>
                p.ParameterType == typeof(IEngine) ? engine
                : given.TryGetValue(p.Name, out var v) ? v
                : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
            var task = (Task)m.Invoke(null, values);
            await task;
            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            return JsonSerializer.SerializeToElement(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        private async Task<DecisionFixture> NorthstarAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "northstar", BuildNorthstarAsync, "Northstar.bim");
            return f;
        }

        /// <summary>The never-saved Northstar model, built the long way. The save tests need it: they prove what the FIRST save of a
        /// model does, and a template copy has already been saved once.</summary>
        private static async Task<DecisionFixture> NorthstarFreshAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await BuildNorthstarAsync(f);
            return f;
        }

        private static async Task BuildNorthstarAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", "CustomerKey", "human");
            await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", "CustomerKey", "human");
            await e.CreateColumnAsync("table:Customer", "City", "String", "City", "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Year", "Int64", "Year", "human");
            await e.CreateColumnAsync("table:Date", "Month", "String", "Month", "human");
        }

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        // ---- 4. set_description covers every object kind a DESC-* rule can flag ------------------------------

        [Fact]
        public async Task Set_description_writes_a_hierarchy_description_and_clears_its_finding()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateHierarchyAsync("table:Date", "Calendar", new[] { "Year", "Month" }, "human");

            var set = await CallToolAsync(f.Engine, "set_description",
                new { objRef = "hierarchy:Date/Calendar", text = "Drill from year to month." });

            Assert.True(set.GetProperty("changed").GetBoolean());
            var card = await f.Engine.AiReadinessScanAsync();
            Assert.DoesNotContain(card.Findings, x => x.RuleId == "DESC-HIERARCHY");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_object_a_description_rule_flags_can_be_described_by_set_description()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateHierarchyAsync("table:Date", "Calendar", new[] { "Year", "Month" }, "human");
            var cg = await f.Engine.CreateCalculationGroupAsync("Time Calc", "human");
            await f.Engine.CreateCalculationItemAsync(cg, "Current", "SELECTEDMEASURE ()", "human");
            await f.Engine.CreateCalculationItemAsync(cg, "Prior Year", "SELECTEDMEASURE ()", "human");
            await Mutate(f, m =>
            {
                m.Tables["Time Calc"].Description = "Switches a measure to Current.";   // omits Prior Year
                m.Tables["Customer"].Columns["City"].Description = "TODO";                // a placeholder
            });

            var flagged = (await f.Engine.AiReadinessScanAsync()).Findings
                .Where(x => x.RuleId.StartsWith("DESC-", StringComparison.Ordinal)).ToArray();
            Assert.Contains(flagged, x => x.RuleId == "DESC-HIERARCHY");
            Assert.Contains(flagged, x => x.RuleId == "DESC-CALCGROUP-ITEMS");

            var refused = new List<string>();
            foreach (var x in flagged.GroupBy(x => x.ObjectRef).Select(g => g.First()))
            {
                try { await f.Engine.SetDescriptionAsync(x.ObjectRef, "Plain business meaning for " + x.ObjectName + ".", "agent"); }
                catch (Exception ex) { refused.Add($"{x.RuleId} {x.ObjectRef}: {ex.Message}"); }
            }
            Assert.True(refused.Count == 0, "set_description refused objects a DESC rule flags: " + string.Join("; ", refused));
        }

        [Fact]
        public async Task The_hierarchy_description_rule_offers_the_description_writer_as_its_fix()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateHierarchyAsync("table:Date", "Calendar", new[] { "Year", "Month" }, "human");

            var finding = Assert.Single((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == "DESC-HIERARCHY");
            Assert.Equal(nameof(FixKind.AiContent), finding.Fix);
            var prompt = await f.Engine.GetFixPromptAsync("DESC-HIERARCHY", finding.ObjectRef);
            Assert.Equal("set_description", prompt.Tool);
            Assert.Contains(finding.ObjectRef, prompt.Prompt, StringComparison.Ordinal);
        }

        // ---- 6. BPA: field parameters, flag format strings, ISO dates ----------------------------------------

        private static readonly string[] FieldParameterExemptRules =
            { "UNNECESSARY_COLUMNS", "DAX_MEASURES_UNQUALIFIED", "REDUCE_USAGE_OF_CALCULATED_TABLES", "ENSURE_TABLES_HAVE_RELATIONSHIPS" };

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Field_parameters_are_exempt_from_the_four_rules_that_misread_them()
        {
            using var f = await NorthstarAsync();
            var e = f.Engine;
            var net = "measure:Sales/Net Sales";
            await e.CreateFieldParameterAsync("Metric Selector", new[] { new FieldParameterItem { ObjectRef = net } }, "human");
            // The form Power BI Desktop itself writes: a table-qualified measure inside NAMEOF.
            await Mutate(f, m => ((CalculatedTable)m.Tables["Metric Selector"]).Expression =
                "{\n    (\"Net Sales\", NAMEOF ( 'Sales'[Net Sales] ), 0)\n}");
            // Controls: the same rules must still fire on ordinary objects.
            await e.CreateCalculatedTableAsync("Scratch", "{ 1 }", "human");
            await e.CreateMeasureAsync("table:Sales", "Net Sales Copy", "1", "human");
            // Written past the DAX write fence, which refuses a table-qualified measure reference outright.
            await Mutate(f, m => m.Tables["Sales"].Measures["Net Sales Copy"].Expression = "'Sales'[Net Sales]");
            await e.CreateColumnAsync("table:Sales", "Legacy Code", "String", "Legacy Code", "human");
            await Mutate(f, m => m.Tables["Sales"].Columns["Legacy Code"].IsHidden = true);

            var v = (await e.BpaScanAsync()).Violations;

            bool OnFieldParameter(BpaViolation x) =>
                x.ObjectRef == "table:Metric Selector" || x.ObjectRef.StartsWith("column:Metric Selector/", StringComparison.Ordinal);
            var wrong = v.Where(x => FieldParameterExemptRules.Contains(x.RuleId) && OnFieldParameter(x))
                .Select(x => x.RuleId + " " + x.ObjectRef).ToArray();
            Assert.True(wrong.Length == 0, "rules still fire on the field parameter: " + string.Join(", ", wrong));

            Assert.Contains(v, x => x.RuleId == "REDUCE_USAGE_OF_CALCULATED_TABLES" && x.ObjectRef == "table:Scratch");
            Assert.Contains(v, x => x.RuleId == "ENSURE_TABLES_HAVE_RELATIONSHIPS" && x.ObjectRef == "table:Scratch");
            Assert.Contains(v, x => x.RuleId == "DAX_MEASURES_UNQUALIFIED" && x.ObjectRef == "measure:Sales/Net Sales Copy");
            Assert.Contains(v, x => x.RuleId == "UNNECESSARY_COLUMNS" && x.ObjectRef == "column:Sales/Legacy Code");
        }

        private const string FlagRule = "FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS";

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_yes_no_format_string_on_an_integer_flag_clears_the_flag_format_rule()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Date", "Is Weekend", "Int64", "IsWeekend", "human");
            await f.Engine.CreateColumnAsync("table:Date", "Is Holiday", "Int64", "IsHoliday", "human");
            Assert.Contains((await f.Engine.BpaScanAsync()).Violations, x => x.RuleId == FlagRule && x.ObjectRef == "column:Date/Is Weekend");

            await Mutate(f, m => m.Tables["Date"].Columns["Is Weekend"].FormatString = "\"Yes\";\"Yes\";\"No\"");

            var v = (await f.Engine.BpaScanAsync()).Violations;
            Assert.DoesNotContain(v, x => x.RuleId == FlagRule && x.ObjectRef == "column:Date/Is Weekend");
            Assert.Contains(v, x => x.RuleId == FlagRule && x.ObjectRef == "column:Date/Is Holiday");   // unformatted still fires
        }

        [Fact]
        public async Task The_flag_format_fix_prompt_names_the_tool_that_can_clear_it()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Date", "Is Weekend", "Int64", "IsWeekend", "human");

            var p = await f.Engine.BpaGetFixPromptAsync(FlagRule, "column:Date/Is Weekend");

            Assert.Contains("set_property", p.Prompt, StringComparison.Ordinal);
            Assert.Contains("FormatString", p.Prompt, StringComparison.Ordinal);
            Assert.Contains("\"Yes\";\"Yes\";\"No\"", p.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("set_measure_format", p.Prompt, StringComparison.Ordinal);
        }

        /// <summary>THE FORMAT FILL RULE (28 Sep): a date format chosen for a person can hide the time a date holds, so
        /// the date rule is advice on a blank date column. It raises it with no one-click fix, a press is refused in words
        /// that name the ISO form it suggests, and the column stays blank. When the person writes the ISO form, or a
        /// regional one, the rule accepts it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_date_rule_advises_the_iso_form_and_accepts_it()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Sales", "Order Date", "DateTime", "OrderDate", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Ship Date", "DateTime", "ShipDate", "human");
            await Mutate(f, m => m.Tables["Sales"].Columns["Ship Date"].FormatString = "mm/dd/yyyy");

            var finding = Assert.Single((await f.Engine.BpaScanAsync()).Violations, x => x.RuleId == "DATECOLUMN_FORMATSTRING");
            Assert.Equal("column:Sales/Order Date", finding.ObjectRef);
            Assert.False(finding.CanAutoFix);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => f.Engine.BpaFixAsync("DATECOLUMN_FORMATSTRING", "column:Sales/Order Date", "agent"));
            Assert.Contains("suggests \"yyyy-mm-dd\"", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("set_measure_format", refused.Message, StringComparison.Ordinal);   // a column's write
            Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns["Order Date"].FormatString));

            await Mutate(f, m => m.Tables["Sales"].Columns["Order Date"].FormatString = "yyyy-mm-dd");
            var v = (await f.Engine.BpaScanAsync()).Violations;
            Assert.DoesNotContain(v, x => x.RuleId == "DATECOLUMN_FORMATSTRING");   // ISO and the US form both pass
        }

        // ---- 7. every tool name a tool description mentions is a real tool ------------------------------------

        /// <summary>snake_case words in descriptions that are NOT tool names, allowed only where they are listed, with
        /// why. Keyed by the tool whose text says them, so a plan-kind word cannot hide a real misnaming elsewhere.</summary>
        private static readonly Dictionary<(string tool, string word), string> NotToolNames = new()
        {
            [("add_plan_item", "set_dax")] = "a plan item kind (a DAX rewrite), not a tool",
            [("add_plan_item", "delete_if_unused")] = "a plan item kind",
            [("set_plan_item", "set_dax")] = "a plan item kind",
            [("set_plan_item", "set_m")] = "a plan item kind (a Power Query body)",
            [("propose_replace", "set_dax")] = "the plan item kind a DAX literal replace becomes",
            [("propose_replace", "set_m")] = "the plan item kind an M replace becomes",
            [("list_report_scope", "delete_if_unused")] = "a plan item kind",
            [("unused_objects", "delete_if_unused")] = "a plan item kind (the reviewed route that removes a visible attribute)",
            [("check_workflow", "bpa_clean")] = "a workflow verify kind",
            [("add_interview_question", "interview_replay")] = "a workflow verify kind",
            [("cicd_generate", "publish_all_items")] = "the fabric-cicd library function the scaffold calls",
            [("cicd_generate", "replace_value")] = "a fabric-cicd parameter.yml key",
            [("publish_data_agent", "publish_info")] = "a field of the Fabric data agent definition",
            [("get_model_plan", "set_aside")] = "a plan state word",
            [("edit_draft", "apply_draft")] = "an edit_draft action",
            [("edit_draft", "ask_again")] = "an edit_draft action",
            [("edit_draft", "check_again")] = "an edit_draft action",
            [("edit_draft", "open_in_author")] = "an edit_draft action",
            [("edit_draft", "try_again")] = "an edit_draft action",
            [("edit_start_plan", "set_link")] = "an edit_start_plan op (a link on or off), not a tool",
            [("edit_start_plan", "set_pick")] = "an edit_start_plan op (how a column adds up), not a tool",
            [("edit_start_plan", "set_fiscal_year_end")] = "an edit_start_plan op (the fiscal year end), not a tool",
            [("edit_start_plan", "set_table_sample")] = "an edit_start_plan op (one table's sample on or off), not a tool",
        };

        [Fact]
        public void Every_tool_name_a_tool_description_mentions_is_in_tools_list()
        {
            var tools = ToolMethods();
            Assert.True(tools.Length >= 300, "the tool surface did not load, so this walk proved nothing");
            var names = new HashSet<string>(tools.Select(ToolName), StringComparer.Ordinal);
            // A word is a tool mention when it is snake_case and starts with the verb of some real tool.
            var verbs = new HashSet<string>(names.Select(n => n.Split('_')[0]), StringComparer.Ordinal);
            var word = new Regex(@"(?<![\w.\\-])[a-z][a-z0-9]*(?:_[a-z0-9]+)+(?![\w-])");

            var bad = new List<string>();
            foreach (var m in tools)
            {
                var tool = ToolName(m);
                var text = string.Join(" ", new[] { m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "" }
                    .Concat(m.GetParameters().Select(p => p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "")));
                foreach (var w in word.Matches(text).Select(x => x.Value).Distinct())
                    if (verbs.Contains(w.Split('_')[0]) && !names.Contains(w) && !NotToolNames.ContainsKey((tool, w)))
                        bad.Add($"{tool} mentions {w}");
            }
            Assert.True(bad.Count == 0, "tool descriptions name tools that do not exist: " + string.Join("; ", bad.OrderBy(x => x, StringComparer.Ordinal)));
        }

        // ---- 8. smaller points -------------------------------------------------------------------------------

        [Fact]
        public async Task Get_object_returns_is_available_in_mdx_for_a_column()
        {
            using var f = await NorthstarAsync();
            await Mutate(f, m => m.Tables["Customer"].Columns["City"].IsAvailableInMDX = false);

            var o = await CallToolAsync(f.Engine, "get_object", new { objRef = "column:Customer/City" });

            Assert.False(o.GetProperty("properties").GetProperty("isAvailableInMdx").GetBoolean());
        }

        [Fact]
        public async Task Validate_dax_strict_makes_a_missing_column_an_error()
        {
            using var f = await NorthstarAsync();
            const string dax = "SUM ( 'Sales'[Gross Amount] )";

            var lenient = await CallToolAsync(f.Engine, "validate_dax", new { expression = dax });
            Assert.True(lenient.GetProperty("valid").GetBoolean());   // the default stays a warning

            var strict = await CallToolAsync(f.Engine, "validate_dax", new { expression = dax, strict = true });
            Assert.False(strict.GetProperty("valid").GetBoolean());
            Assert.Contains(strict.GetProperty("diagnostics").EnumerateArray(),
                d => d.GetProperty("severity").GetString() == "error"
                     && d.GetProperty("message").GetString().Contains("Gross Amount", StringComparison.Ordinal));

            var fine = await CallToolAsync(f.Engine, "validate_dax", new { expression = "SUM ( Sales[Amount] )", strict = true });
            Assert.True(fine.GetProperty("valid").GetBoolean());
        }

        [Fact]
        public async Task A_visible_dimension_attribute_with_no_model_reference_is_never_safe_to_delete()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Customer", "Legacy Code", "String", "Legacy Code", "human");
            await Mutate(f, m => m.Tables["Customer"].Columns["Legacy Code"].IsHidden = true);

            await f.Engine.SetReportScopeAsync(Array.Empty<Semanticus.Engine.Lineage.ReportScopeChoice>(), "human");   // the model has no reports: report-aware over an empty set
            var u = await CallToolAsync(f.Engine, "unused_objects");
            var items = u.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("ref").GetString(), i => i);

            var city = items["column:Customer/City"];
            Assert.Equal("visibleAttribute", city.GetProperty("verdict").GetString());
            Assert.Contains("Visible attribute, no model reference", city.GetProperty("reason").GetString(), StringComparison.Ordinal);
            Assert.Equal("safe", items["column:Customer/Legacy Code"].GetProperty("verdict").GetString());   // hidden: still safe
            Assert.Equal(1, u.GetProperty("visibleAttributeCount").GetInt32());

            var removed = await f.Engine.RemoveSafeObjectsAsync(null, null, "agent");
            Assert.DoesNotContain(removed.Removed, r => r.Ref == "column:Customer/City");
            Assert.True(await f.Sessions.Require().ReadAsync(m => m.Tables["Customer"].Columns.Contains("City")));
        }

        [Fact]
        public async Task The_readiness_summary_hides_a_category_that_scored_nothing_and_says_why()
        {
            using var f = await NorthstarAsync();

            var card = await f.Engine.AiReadinessScanAsync();
            var empty = card.Categories.Where(c => !c.HasRules).Select(c => c.Category).ToArray();
            Assert.NotEmpty(empty);   // otherwise this test proves nothing

            var s = await CallToolAsync(f.Engine, "ai_readiness_scan", new { detail = "summary" });
            var shown = s.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("category").GetString()).ToArray();
            Assert.Empty(shown.Intersect(empty));
            var notScored = s.GetProperty("notScored").EnumerateArray().ToArray();
            Assert.Equal(empty.OrderBy(x => x), notScored.Select(n => n.GetProperty("category").GetString()).OrderBy(x => x));
            Assert.All(notScored, n => Assert.Contains("not counted", n.GetProperty("reason").GetString(), StringComparison.Ordinal));

            var full = await CallToolAsync(f.Engine, "ai_readiness_scan", new { detail = "full" });
            foreach (var c in full.GetProperty("categories").EnumerateArray().Where(c => !c.GetProperty("hasRules").GetBoolean()))
                Assert.Contains("not counted", c.GetProperty("notScoredReason").GetString(), StringComparison.Ordinal);
        }

        /// <summary>Every tool stamp in the MODEL files the save wrote. The engine's own sidecar folder is skipped:
        /// the edit ledger writes `.semanticus/history.json` through a `history.json.tmp` rename AFTER the save
        /// returns (EditLedger.cs), so listing the folder and then reading each path raced that rename and threw
        /// FileNotFoundException on the temp file in a run where nothing under test had changed. It is not a model
        /// file and it never carries a stamp, so it is not part of the question these tests ask.</summary>
        private static string[] ToolStamps(string root) =>
            (File.Exists(root) ? new[] { root } : Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                .Where(p => !IsEngineSidecar(root, p))
                .SelectMany(p => new[] { "__TEdtr", "TabularEditor_SerializeOptions" }
                    .Where(s => File.ReadAllText(p).Contains(s, StringComparison.Ordinal))
                    .Select(s => Path.GetFileName(p) + ": " + s))
                .ToArray();

        private static bool IsEngineSidecar(string root, string path) =>
            Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(seg => string.Equals(seg, ".semanticus", StringComparison.OrdinalIgnoreCase));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("TMDL")]
        [InlineData("bim")]
        public async Task Saving_adds_no_tool_annotations_the_model_did_not_have(string format)
        {
            using var f = await NorthstarFreshAsync();
            var first = Path.Combine(f.Workspace, format == "bim" ? "Northstar.bim" : "Northstar");
            await f.Engine.SaveAsync(first, format, false, "human");
            Assert.Empty(ToolStamps(first));

            // The first save must not leave a stamp in memory that a later save or export writes out.
            var second = Path.Combine(f.Workspace, "Second.bim");
            await f.Engine.SaveAsync(second, "bim", false, "human");
            Assert.Empty(ToolStamps(second));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("TMDL")]
        [InlineData("bim")]
        public async Task Saving_keeps_tool_annotations_a_model_already_had(string format)
        {
            using var f = await NorthstarFreshAsync();
            await Mutate(f, m => m.SetAnnotation("TabularEditor_SerializeOptions", "{\"IgnoreTimestamps\":true}"));
            var path = Path.Combine(f.Workspace, format == "bim" ? "Kept.bim" : "Kept");

            await f.Engine.SaveAsync(path, format, false, "human");

            Assert.Contains(ToolStamps(path), s => s.EndsWith("TabularEditor_SerializeOptions", StringComparison.Ordinal));
        }

        /// <summary>The two save tests above were flaky: their scan listed the engine's own `.semanticus` folder and
        /// then read each path, racing the edit ledger's `history.json.tmp` rename, which lands after the save returns
        /// (FileNotFoundException, seen once in a run where nothing under test had changed). The race cannot be forced,
        /// but the thing that fixes it can: the scan must not read that folder at all. Planting a stamp there is the
        /// deterministic proof, because the old scan would have reported it.</summary>
        [Fact]
        public async Task The_save_stamp_scan_never_reads_the_engines_own_sidecar_folder()
        {
            using var f = await NorthstarFreshAsync();
            var root = Path.Combine(f.Workspace, "Sidecar");
            await f.Engine.SaveAsync(root, "TMDL", false, "human");

            var sidecar = Path.Combine(root, ".semanticus");
            Directory.CreateDirectory(sidecar);
            File.WriteAllText(Path.Combine(sidecar, "history.json.tmp"), "{\"annotations\":[\"__TEdtr\"]}");

            Assert.Contains(Directory.GetFiles(root, "*", SearchOption.AllDirectories),
                p => p.Contains(Path.DirectorySeparatorChar + ".semanticus" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            Assert.Empty(ToolStamps(root));
        }

        [Fact]
        public async Task The_ai_instructions_finding_cites_microsofts_prep_data_for_ai_page()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateLinguisticSchemaAsync("en-US", "human");

            var finding = Assert.Single((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == "DAC-AI-INSTRUCTIONS");

            Assert.Contains("learn.microsoft.com/power-bi/create-reports/copilot-prepare-data-ai", finding.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("http", finding.Message, StringComparison.Ordinal);   // the engine carries no fetchable URL
        }

        // ---- round 2: the supporting changes the first round shipped untested, plus the apply_fix guard --------
        // Each of these five was reverted on a scratch copy and watched failing before it was written down here
        // (the verifier's mutation switches M2i, M14, M15 and M16, plus the new BpaFixAsync guard).

        /// <summary>M14. The DAX-rewrite prompt named "update_measure/set_dax", and set_dax is a plan item kind, not
        /// a tool. An assistant that reads a tool name out of a prompt and calls it gets "unknown tool".</summary>
        [Theory]
        [InlineData("BP-DAX-IFERROR-DIV")]
        [InlineData("BP-DAX-ISBLANK-EQ")]
        public async Task The_dax_rewrite_fix_prompt_names_only_a_tool_that_exists(string ruleId)
        {
            using var f = await NorthstarAsync();

            var p = await f.Engine.GetFixPromptAsync(ruleId, "measure:Sales/Net Sales");

            Assert.Contains("update_measure", p.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("set_dax", p.Prompt, StringComparison.Ordinal);   // a plan item kind, never a tool
            Assert.Equal("verify_dax_equivalence", p.Tool);
        }

        /// <summary>M15. Grounding had no Hierarchy case, so the writer of a hierarchy description was handed no
        /// table and no levels: the one thing a drill path's description has to explain.</summary>
        [Fact]
        public async Task Grounding_for_a_hierarchy_carries_its_table_and_its_levels_in_order()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateHierarchyAsync("table:Date", "Calendar", new[] { "Year", "Month" }, "human");

            var g = await f.Engine.GetGroundingAsync("hierarchy:Date/Calendar");

            Assert.Equal("Date", g.Table);
            Assert.Equal(new[] { "Year", "Month" }, g.SiblingNames);
            var prompt = await f.Engine.GetFixPromptAsync("DESC-HIERARCHY", "hierarchy:Date/Calendar");
            Assert.Contains("Year, Month", prompt.Prompt, StringComparison.Ordinal);
        }

        /// <summary>M16. add_plan_item read the before-value of a set_description off Measure/Table/Column only, so a
        /// hierarchy's existing text showed as "(none)" and the person reviewing the change saw no diff.</summary>
        [Fact]
        public async Task A_planned_description_carries_a_hierarchys_current_text_as_its_before_value()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateHierarchyAsync("table:Date", "Calendar", new[] { "Year", "Month" }, "human");
            await f.Engine.SetDescriptionAsync("hierarchy:Date/Calendar", "Old drill path.", "human");

            var plan = await f.Engine.AddPlanItemAsync("hierarchy:Date/Calendar", "set_description", "New drill path.",
                "Describe Calendar", null, null, "human");

            var item = plan.Items.Last(i => i.ObjectRef == "hierarchy:Date/Calendar");
            Assert.Equal("Old drill path.", item.Before);
        }

        /// <summary>M2i. The hierarchy's readiness finding offers set_description as its fix, so the plan door has to
        /// carry the same item: propose_plan seeds it, and applying it writes the description.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_hierarchy_description_finding_becomes_a_plan_item_that_applies()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateHierarchyAsync("table:Date", "Calendar", new[] { "Year", "Month" }, "human");

            var seeded = await f.Engine.ProposePlanAsync(null, includeAi: true, maxAiItems: 200, "human");

            var item = Assert.Single(seeded.Items, i => i.RuleId == "DESC-HIERARCHY");
            Assert.Equal("set_description", item.Kind);
            Assert.Equal("hierarchy:Date/Calendar", item.ObjectRef);

            await f.Engine.SetPlanItemAsync(item.Id, "Drill from year to month.", approved: true, "human");
            var report = await f.Engine.ApplyPlanAsync(new[] { item.Id }, "human");

            Assert.Equal("applied", report.Items.Single(i => i.Id == item.Id).Status);
            var written = await f.Sessions.Require().ReadAsync(m => m.Tables["Date"].Hierarchies["Calendar"].Description);
            Assert.Equal("Drill from year to month.", written);
            Assert.DoesNotContain((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == "DESC-HIERARCHY");
        }

        /// <summary>The row the cleanup list prints beside an ENABLED Propose removal button. It may say a sweep never
        /// takes the column; it may not say the removal is not offered, because a reviewed removal does remove it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_visible_attribute_row_never_denies_the_removal_the_card_offers()
        {
            using var f = await NorthstarAsync();
            await f.Engine.SetReportScopeAsync(Array.Empty<Semanticus.Engine.Lineage.ReportScopeChoice>(), "human");   // the model has no reports: report-aware over an empty set

            var u = await CallToolAsync(f.Engine, "unused_objects");
            var city = u.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("ref").GetString() == "column:Customer/City");
            var reason = city.GetProperty("reason").GetString();

            Assert.Equal("visibleAttribute", city.GetProperty("verdict").GetString());
            Assert.DoesNotContain("not offered for removal", reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cannot be removed", reason, StringComparison.OrdinalIgnoreCase);

            // And that is true, on the journey the card starts: propose, approve, apply.
            var plan = await f.Engine.AddPlanItemAsync("column:Customer/City", "delete_if_unused", null, "Remove City", null, null, "human");
            var id = plan.Items.Last(i => i.ObjectRef == "column:Customer/City").Id;
            await f.Engine.SetPlanItemAsync(id, null, approved: true, "human");
            var report = await f.Engine.ApplyPlanAsync(new[] { id }, "human");
            Assert.Equal("applied", report.Items.Single(i => i.Id == id).Status);
            Assert.Null(await f.Sessions.Require().ReadAsync(m => m.Tables["Customer"].Columns.FirstOrDefault(c => c.Name == "City")));

            // A sweep still never takes one by itself, named or not. City is gone here, so a fresh model proves it.
            using var g = await NorthstarAsync();
            var swept = await g.Engine.RemoveSafeObjectsAsync(null, null, "human");
            Assert.DoesNotContain(swept.Removed, r => r.Ref == "column:Customer/City");
            var named = await g.Engine.RemoveSafeObjectsAsync(new[] { "column:Customer/City" }, null, "agent");
            var skip = Assert.Single(named.Skipped, k => k.Ref == "column:Customer/City");
            Assert.Equal(0, named.Count);
            Assert.DoesNotContain("could not be fully verified", skip.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("delete_if_unused", skip.Reason, StringComparison.Ordinal);   // it names the door that does it
        }

        /// <summary>A deterministic fix exists to clear a finding. apply_fix used to write it at any object it was
        /// pointed at, so the date rule's one-click fix would replace a person's Long Date with the ISO form on a
        /// column the rule does not flag. That is the half of the date rule's promise the expression cannot keep.
        /// Re-applying a fix that already landed is not that case and stays a quiet no-op, so a second call is
        /// still idempotent (AirSmoke applies the same fix twice from one scan).</summary>
        [Fact]
        public async Task Apply_fix_refuses_to_overwrite_a_column_the_rule_does_not_flag()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Sales", "Order Date", "DateTime", "OrderDate", "human");
            await Mutate(f, m => m.Tables["Sales"].Columns["Order Date"].FormatString = "Long Date");
            Assert.DoesNotContain((await f.Engine.BpaScanAsync()).Violations,
                x => x.RuleId == "DATECOLUMN_FORMATSTRING" && x.ObjectRef == "column:Sales/Order Date");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Engine.BpaFixAsync("DATECOLUMN_FORMATSTRING", "column:Sales/Order Date", "agent"));

            Assert.Contains("does not violate", ex.Message, StringComparison.Ordinal);
            var still = await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns["Order Date"].FormatString);
            Assert.Equal("Long Date", still);

            // The fix that DID clear a finding stays repeatable: a second call re-writes the same value instead of
            // refusing, which is what a caller holding one scan does (AirSmoke fixes twice from the same list). The one
            // fix a shipped rule still makes is the integer rule's fill of a blank count (THE FORMAT FILL RULE).
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Rows", "COUNTROWS ( Sales )", "human");
            Assert.True((await f.Engine.BpaFixAsync("INTEGER_FORMATTING", "measure:Sales/Order Rows", "agent")).Changed);
            Assert.False((await f.Engine.BpaFixAsync("INTEGER_FORMATTING", "measure:Sales/Order Rows", "agent")).Changed);
            Assert.Equal("#,0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Order Rows"].FormatString));
        }

        // ---- round 3: found means fixed (the coordinator's three, 27 Sep) -----------------------------------

        /// <summary>Every other plan kind gets the stale-value guard ("This value changed after the plan was built"),
        /// because LiveValueForPlanItem knows how to read its target. bpa_fix had no case, so it read null and the
        /// guard never fired: a fix proposed against an empty format string overwrote whatever the person put there
        /// afterwards. The normalisation matters as much as the case — MapBpaFix stores an absent value as "(none)"
        /// while the live read is "", so without it the guard would fire on EVERY bpa_fix and nothing would apply.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_planned_bpa_fix_is_skipped_when_the_person_changed_that_value_after_proposing_it()
        {
            using var f = await NorthstarAsync();
            // The one blank format a shipped rule still fills is a count (THE FORMAT FILL RULE), so that is the vehicle.
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Rows", "COUNTROWS ( Sales )", "human");

            var plan = await f.Engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "human");
            var item = Assert.Single(plan.Items, i => i.Kind == "bpa_fix" && i.RuleId == "INTEGER_FORMATTING");
            Assert.Equal("measure:Sales/Order Rows", item.ObjectRef);
            // "No value here" is what both sides have to agree on. TOM answers "" for an unset FormatString, so that
            // is what the item records; PreviewFix writes "(none)" when a property really is null, and PlanTextEquals
            // normalises the two, which is why the live read below answers "" rather than null for an absent value.
            Assert.Equal("", item.Before);
            Assert.Equal("#,0", item.After);

            // The person formats the measure themselves, in the window between proposing and applying.
            await Mutate(f, m => m.Tables["Sales"].Measures["Order Rows"].FormatString = "0");

            var report = await f.Engine.ApplyPlanAsync(new[] { item.Id }, "human");

            var applied = report.Items.Single(i => i.Id == item.Id);
            Assert.Equal("skipped", applied.Status);
            Assert.Contains("changed after the plan was built", applied.Note, StringComparison.Ordinal);
            Assert.Equal("0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Order Rows"].FormatString));
        }

        /// <summary>The other half of the same guard: "(none)" and an empty live value are the SAME value, so a
        /// bpa_fix nobody touched still applies. Without the normalisation the guard would skip every one of them.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_planned_bpa_fix_nobody_touched_still_applies()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Rows", "COUNTROWS ( Sales )", "human");

            var plan = await f.Engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "human");
            var item = Assert.Single(plan.Items, i => i.Kind == "bpa_fix" && i.RuleId == "INTEGER_FORMATTING");

            var report = await f.Engine.ApplyPlanAsync(new[] { item.Id }, "human");

            Assert.Equal("applied", report.Items.Single(i => i.Id == item.Id).Status);
            Assert.Equal("#,0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Order Rows"].FormatString));
        }

        /// <summary>ApplyFix answered true whenever it wrote, so re-applying a fix that already landed reported
        /// changed:true for a write of the value that was already there. An assistant reading that answer is told the
        /// model moved when it did not, and apply_safe_fixes counted the same no-op as work in every sweep.</summary>
        [Fact]
        public async Task Applying_a_fix_that_is_already_in_place_answers_changed_false()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Rows", "COUNTROWS ( Sales )", "human");

            Assert.True((await f.Engine.BpaFixAsync("INTEGER_FORMATTING", "measure:Sales/Order Rows", "agent")).Changed);
            var again = await f.Engine.BpaFixAsync("INTEGER_FORMATTING", "measure:Sales/Order Rows", "agent");

            Assert.False(again.Changed);
            Assert.Equal("#,0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Order Rows"].FormatString));
        }

        /// <summary>A calculation group's own column is how people pick a calculation: remove it and the group still
        /// exists but nothing can filter by it, and the model saves that way. The sweep called it "safe" (nothing in
        /// the model references it by name, which is true and beside the point) and remove_safe_objects deleted it.
        /// Proven on BOTH doors of one session: the MCP tool an assistant calls and the RPC target Studio calls.</summary>
        [Fact]
        public async Task A_calculation_groups_own_column_is_never_called_safe_and_is_never_swept_away()
        {
            using var f = await NorthstarAsync();
            var cg = await f.Engine.CreateCalculationGroupAsync("Time Intelligence", "human");
            await f.Engine.CreateCalculationItemAsync(cg, "Current", "SELECTEDMEASURE ()", "human");
            var pickerRef = await f.Sessions.Require().ReadAsync(m =>
                ObjectRefs.For(((CalculationGroupTable)m.Tables["Time Intelligence"]).Columns.First(c => !c.IsHidden)));
            var rpc = new EngineRpcTarget(f.Engine);   // the door Studio calls

            // Door 1 (MCP) and door 2 (RPC) must agree, and neither may call it safe.
            var mcp = await CallToolAsync(f.Engine, "unused_objects");
            var mcpHit = mcp.GetProperty("items").EnumerateArray()
                .FirstOrDefault(i => i.GetProperty("ref").GetString() == pickerRef);
            Assert.True(mcpHit.ValueKind == JsonValueKind.Undefined,
                $"the MCP sweep offers {pickerRef} as a candidate: {(mcpHit.ValueKind == JsonValueKind.Undefined ? "" : mcpHit.ToString())}");
            Assert.DoesNotContain((await rpc.unusedObjects()).Items, i => i.Ref == pickerRef);

            // Neither door removes it: named explicitly, or swept with no refs at all.
            foreach (var (door, report) in new[]
            {
                ("mcp", await f.Engine.RemoveSafeObjectsAsync(new[] { pickerRef }, null, "agent")),
                ("rpc", await rpc.removeSafeObjects(new[] { pickerRef })),
            })
            {
                Assert.Equal(0, report.Count);
                var skip = Assert.Single(report.Skipped, k => k.Ref == pickerRef);
                Assert.Contains("calculation group", skip.Reason, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("could not be fully verified", skip.Reason, StringComparison.OrdinalIgnoreCase);
                Assert.True(door.Length > 0);
            }
            Assert.DoesNotContain((await f.Engine.RemoveSafeObjectsAsync(null, null, "human")).Removed, r => r.Ref == pickerRef);

            // And the plan door, which is where an approved removal would actually run.
            var staged = await f.Engine.AddPlanItemAsync(pickerRef, "delete_if_unused", null, "Remove the picker", null, null, "human");
            var id = staged.Items.Last(i => i.ObjectRef == pickerRef).Id;
            await f.Engine.SetPlanItemAsync(id, null, approved: true, "human");
            var applyReport = await f.Engine.ApplyPlanAsync(new[] { id }, "human");
            var line = applyReport.Items.Single(i => i.Id == id);
            Assert.Equal("skipped", line.Status);
            Assert.Contains("calculation group", line.Note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no longer holds", line.Note, StringComparison.OrdinalIgnoreCase);

            // The picker survived all of it, so the group can still be filtered by.
            Assert.NotNull(await f.Sessions.Require().ReadAsync(m => ObjectRefs.Resolve(m, pickerRef)));
        }

        // ---- round 3 findings (V1-V4) --------------------------------------------------------------------------

        /// <summary>V1. The date rule was corrected so that any real date format passes and its one-click fix can
        /// only fill an empty format string. Its sibling, the MONTH rule, kept the old shape one rule below it, so
        /// Semanticus flagged a column for carrying the ISO format its own date fix writes and one press replaced it.
        /// This is the whole-file guard rail rather than the single case: EVERY shipped rule scoped at a column whose
        /// fix writes FormatString must fire only on a column that has no format string. A third such rule added
        /// later, or either of these two loosened again, fails here.
        /// The two Measure-scoped rules, PERCENTAGE_FORMATTING and INTEGER_FORMATTING, are pinned here as that pair
        /// and are guarded by their own tests below: they are about a house style for numbers, so they are allowed to
        /// tidy a format string, but only one they can prove is a number. The measure half of V1 was reported rather
        /// than fixed in round 3 and was fixed in round 5, so this test no longer records it as known and unfixed.</summary>
        [Fact]
        public void No_shipped_column_format_rule_replaces_a_format_string_that_is_already_there()
        {
            var writesFormat = BpaRuleSet.Standard()
                .Where(r => (r.FixExpression ?? "").Replace(" ", "").StartsWith("FormatString=", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var onColumns = writesFormat.Where(r => (r.Scope ?? "").IndexOf("Column", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var onMeasures = writesFormat.Except(onColumns).Select(r => r.ID).OrderBy(x => x, StringComparer.Ordinal).ToArray();

            Assert.Equal(new[] { "INTEGER_FORMATTING", "PERCENTAGE_FORMATTING" }, onMeasures);
            Assert.Equal(
                new[] { "DATECOLUMN_FORMATSTRING", "MONTHCOLUMN_FORMATSTRING" },
                onColumns.Select(r => r.ID).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            foreach (var r in onColumns)
            {
                var expr = string.Concat((r.Expression ?? "").Where(c => !char.IsWhiteSpace(c)));
                Assert.True(expr.Contains("FormatString=nullorFormatString.Trim()=\"\"", StringComparison.Ordinal),
                    $"{r.ID} can fire on a column that already has a format string, so its fix would replace one: {r.Expression}");
            }
        }

        /// <summary>V1, the live half and the control. A month column carrying any real date format is left alone on
        /// both halves of the press (the scan does not raise it, and apply_fix refuses to write over it), and an
        /// unformatted month column is still flagged. Since THE FORMAT FILL RULE (28 Sep) that flag is advice: a format
        /// chosen for a person can hide what the column holds, so the press is refused in words naming "MMMM yyyy" and
        /// the column stays blank.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("yyyy-mm-dd")]
        [InlineData("Long Date")]
        [InlineData("mmm yyyy")]
        public async Task The_month_rule_leaves_a_real_date_format_alone_and_advises_on_an_empty_one(string existing)
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Date", "Month Start", "DateTime", "MonthStart", "human");
            await f.Engine.CreateColumnAsync("table:Date", "Month End", "DateTime", "MonthEnd", "human");
            await Mutate(f, m => m.Tables["Date"].Columns["Month Start"].FormatString = existing);

            var v = (await f.Engine.BpaScanAsync()).Violations
                .Where(x => x.RuleId == "MONTHCOLUMN_FORMATSTRING").Select(x => x.ObjectRef).ToArray();
            Assert.Equal(new[] { "column:Date/Month End" }, v);

            // The one the rule does not flag is refused, by name, rather than quietly overwritten.
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => f.Engine.BpaFixAsync("MONTHCOLUMN_FORMATSTRING", "column:Date/Month Start", "agent"));
            Assert.Contains("would overwrite FormatString", refused.Message, StringComparison.Ordinal);

            // The one it does flag is advice: refused in words that name the suggestion, and nothing is written.
            var advice = await Assert.ThrowsAsync<InvalidOperationException>(
                () => f.Engine.BpaFixAsync("MONTHCOLUMN_FORMATSTRING", "column:Date/Month End", "agent"));
            Assert.Contains("suggests \"MMMM yyyy\"", advice.Message, StringComparison.Ordinal);
            await f.Engine.BpaFixAllAsync("human");
            var (start, end) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Date"].Columns["Month Start"].FormatString, m.Tables["Date"].Columns["Month End"].FormatString));
            Assert.Equal(existing, start);
            Assert.Equal("", end);
        }

        // ---- V1b, the measure half: a format rule may only tidy a format string it can PROVE is a number -------

        /// <summary>V1b. The two column rules were corrected in round 3 so a real format is never written over, and
        /// INTEGER_FORMATTING carried the identical hazard one object kind across: upstream's expression has no guard
        /// at all, so a measure formatted "Long Date" was raised by a rule called "Whole numbers should be formatted
        /// with thousands separators and no decimals" and one press replaced the date format with "#,0". It now fires
        /// only on a format string it can PROVE is a number (round 7: made only of the plain-number grammar, with at
        /// least one 0 or # placeholder) or on no format at all, so a real date or time format is left alone on both halves of
        /// the press: the scan does not raise it, and apply_fix refuses by name rather than overwriting it.</summary>
        private static readonly string[] DateTimeFormatCases =
        {
            ("Long Date"),
            ("Short Date"),
            ("General Date"),
            ("Long Time"),
            ("hh:nn:ss"),
            ("dd/mm/yyyy hh:nn"),
            ("yyyy-mm-dd"),
        };

        [Fact]
        public async Task The_integer_rule_leaves_a_real_date_or_time_format_alone()
        {
            var cases = DateTimeFormatCases;
            using var f = await NorthstarAsync();
            for (var i = 0; i < cases.Length; i++) await f.Engine.CreateMeasureAsync("table:Sales", "Last Order Date " + i, "MAX ( Sales[Amount] )", "human");
            await Mutate(f, m => { for (var i = 0; i < cases.Length; i++) m.Tables["Sales"].Measures["Last Order Date " + i].FormatString = cases[i]; });

            var card = await f.Engine.BpaScanAsync();
            for (var i = 0; i < cases.Length; i++)
            {
                var hits = card.Violations.Where(v => v.ObjectRef == "measure:Sales/Last Order Date " + i && v.RuleId == "INTEGER_FORMATTING").ToArray();
                Assert.True(hits.Length == 0, $"a measure formatted \"{cases[i]}\" is raised by INTEGER_FORMATTING, whose fix writes \"#,0\"");
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => f.Engine.BpaFixAsync("INTEGER_FORMATTING", "measure:Sales/Last Order Date " + i, "agent"));
                Assert.Contains("would overwrite FormatString", refused.Message, StringComparison.Ordinal);
            }
            var now = await f.Sessions.Require().ReadAsync(m => cases.Select((c, i) => m.Tables["Sales"].Measures["Last Order Date " + i].FormatString).ToArray());
            for (var i = 0; i < cases.Length; i++) Assert.True(cases[i] == now[i], $"\"{cases[i]}\" was rewritten to \"{now[i]}\"");
        }

        /// <summary>V1b, the control, under the round-8 ruling (28 Sep): a fix never writes over a format string that
        /// is already there. Narrowing the rule must not switch it off, so a plain number format that is not the house
        /// style is still RAISED, but as advice: it offers no one-click fix and a press is refused in words that name
        /// what the rule suggests. A measure with no format string at all is still raised; one press fills it only when
        /// the engine proves it is a whole number (THE FORMAT FILL RULE, 28 Sep: here a DISTINCTCOUNT), and the house
        /// style it writes is not raised again. A blank SUM of a decimal column ("Net Sales") is advice: #,0 would drop
        /// its cents.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_integer_rule_advises_a_plain_number_format_and_fills_only_an_empty_count()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Customers", "DISTINCTCOUNT ( Sales[CustomerKey] )", "human");
            await Mutate(f, m => m.Tables["Sales"].Measures["Order Count"].FormatString = "0.00");

            (string, bool)[] Flagged(BpaScorecard c) => c.Violations
                .Where(v => v.RuleId == "INTEGER_FORMATTING").Select(v => (v.ObjectRef, v.CanAutoFix))
                .OrderBy(x => x.ObjectRef, StringComparer.Ordinal).ToArray();

            // "0.00" is provably a number and is not the house style; "Customers" and "Net Sales" carry no format string.
            Assert.Equal(new[] { ("measure:Sales/Customers", true), ("measure:Sales/Net Sales", false), ("measure:Sales/Order Count", false) },
                Flagged(await f.Engine.BpaScanAsync()));

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => f.Engine.BpaFixAsync("INTEGER_FORMATTING", "measure:Sales/Order Count", "agent"));
            Assert.Contains("only fills a blank format string", refused.Message, StringComparison.Ordinal);
            Assert.Contains("suggests \"#,0\"", refused.Message, StringComparison.Ordinal);
            var money = await Assert.ThrowsAsync<InvalidOperationException>(
                () => f.Engine.BpaFixAsync("INTEGER_FORMATTING", "measure:Sales/Net Sales", "agent"));
            Assert.Contains("It has no format yet", money.Message, StringComparison.Ordinal);
            Assert.Contains("suggests \"#,0\"", money.Message, StringComparison.Ordinal);
            Assert.True((await f.Engine.BpaFixAsync("INTEGER_FORMATTING", "measure:Sales/Customers", "agent")).Changed);

            var (count, net, customers) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Order Count"].FormatString, m.Tables["Sales"].Measures["Net Sales"].FormatString,
                 m.Tables["Sales"].Measures["Customers"].FormatString));
            Assert.Equal("0.00", count);
            Assert.Equal("", net);
            Assert.Equal("#,0", customers);
            Assert.Equal(new[] { ("measure:Sales/Net Sales", false), ("measure:Sales/Order Count", false) }, Flagged(await f.Engine.BpaScanAsync()));
        }

        /// <summary>V1b, the sibling rule, under the round-8 ruling. PERCENTAGE_FORMATTING only raises a format that
        /// already has a % in it, so its fix is never the fill of a blank one and never applies: every percentage it
        /// raises is advice. It still needs the grammar's proof to raise at all (a %, nothing outside the plain-number
        /// grammar, and a 0 or # placeholder), so a percentage a person wrote words into is not raised, and a press on
        /// it is refused as a write over a finding that is not there.</summary>
        [Fact]
        public async Task The_percentage_rule_only_advises_a_format_it_can_prove_is_a_percentage()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin Pct", "DIVIDE ( [Net Sales], 1 )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Share Of Plan", "DIVIDE ( [Net Sales], 1 )", "human");
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Margin Pct"].FormatString = "0%";
                m.Tables["Sales"].Measures["Share Of Plan"].FormatString = "0.0%\" of plan\"";
            });

            (string, bool)[] Flagged(BpaScorecard c) => c.Violations
                .Where(v => v.RuleId == "PERCENTAGE_FORMATTING").Select(v => (v.ObjectRef, v.CanAutoFix))
                .OrderBy(x => x.ObjectRef, StringComparer.Ordinal).ToArray();

            Assert.Equal(new[] { ("measure:Sales/Margin Pct", false) }, Flagged(await f.Engine.BpaScanAsync()));

            // The one it raises is advice: the press is refused and names the house style it suggests.
            var advice = await Assert.ThrowsAsync<InvalidOperationException>(
                () => f.Engine.BpaFixAsync("PERCENTAGE_FORMATTING", "measure:Sales/Margin Pct", "agent"));
            Assert.Contains("suggests \"#,0.0%;-#,0.0%;#,0.0%\"", advice.Message, StringComparison.Ordinal);

            // The one it does not raise is refused by name rather than quietly overwritten.
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => f.Engine.BpaFixAsync("PERCENTAGE_FORMATTING", "measure:Sales/Share Of Plan", "agent"));
            Assert.Contains("would overwrite FormatString", refused.Message, StringComparison.Ordinal);

            var (margin, share) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Margin Pct"].FormatString, m.Tables["Sales"].Measures["Share Of Plan"].FormatString));
            Assert.Equal("0%", margin);
            Assert.Equal("0.0%\" of plan\"", share);
        }

        // ---- V1c: prove what a format IS, never what it is not ------------------------------------------------

        /// <summary>Every shipped rule whose one-click fix writes FormatString, whatever it is scoped at.</summary>
        private static string[] FormatWritingRules() => BpaRuleSet.Standard()
            .Where(r => (r.FixExpression ?? "").Replace(" ", "").StartsWith("FormatString=", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ID).ToArray();

        /// <summary>The rules these tests hold to the grammar: every format-writing rule, plus both measure format
        /// rules by id, so a rule that only gives advice is still held to "never raised outside the grammar".</summary>
        private static string[] FormatRules() =>
            FormatWritingRules().Concat(new[] { "INTEGER_FORMATTING", "PERCENTAGE_FORMATTING" }).Distinct().ToArray();

        private static async Task<string[]> WritersFlaggingAsync(DecisionFixture f, string objRef) =>
            (await f.Engine.BpaScanAsync()).Violations
                .Where(v => v.ObjectRef == objRef && FormatRules().Contains(v.RuleId))
                .Select(v => v.RuleId).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        /// <summary>The same findings with whether each one offers a one-click fix.</summary>
        private static async Task<(string rule, bool fix)[]> FormatFindingsAsync(DecisionFixture f, string objRef) =>
            (await f.Engine.BpaScanAsync()).Violations
                .Where(v => v.ObjectRef == objRef && FormatRules().Contains(v.RuleId))
                .Select(v => (v.RuleId, v.CanAutoFix)).OrderBy(x => x.RuleId, StringComparer.Ordinal).ToArray();

        /// <summary>Press every format-writing fix on one object (a refusal is the expected answer), then Fix all.</summary>
        private static async Task PressEverythingAsync(DecisionFixture f, string objRef)
        {
            await PressEverythingOnceAsync(f, objRef);
            await f.Engine.BpaFixAllAsync("human");
        }

        /// <summary>The presses of <see cref="PressEverythingAsync"/> without the Fix all, for a corpus that presses many objects and then runs Fix all once.</summary>
        private static async Task PressEverythingOnceAsync(DecisionFixture f, string objRef)
        {
            foreach (var rule in FormatWritingRules())
            {
                try { await f.Engine.BpaFixAsync(rule, objRef, "agent"); }
                catch (InvalidOperationException) { /* refusing the press is the point */ }
            }
        }

        /// <summary>V1c (D2, D3 and the M3 gap). Rounds 5 and 6 tried to prove what a format is NOT: no Latin letter, no
        /// dollar sign. Every currency but the dollar, and every letter outside A to Z, slipped through, and one press
        /// wrote "#,0" over them. The measure rules now prove what a format IS: a plain-number grammar of 0 # , . ; ( )
        /// - % and spaces, with a quoted piece holding only that punctuation. Each case here is outside that grammar
        /// and is chosen to pin one leg of it. "hh:nn:ss.00" and "mm:ss.0" are time formats that carry a 0, so the
        /// placeholder check alone cannot keep them safe (the M3 gap: deleting the letter or grammar guard failed no
        /// test). "0\"%\"" shows a literal percent sign without scaling the number, so a percentage house style would
        /// multiply it by a hundred. "\"0\"" is a quoted zero, which is text, not a placeholder. ";;;" and "%" carry no
        /// placeholder at all.</summary>
        private static readonly string[] OutsideGrammarCases =
        {
            ("hh:nn:ss.00"),
            ("mm:ss.0"),
            ("#,0 ₽"),
            ("₩#,0"),
            ("#,0.00 kr"),
            ("0.0°"),
            ("#,0 ‰"),
            ("#,0 \"days\""),
            ("0.00E+00"),
            ("\\-#,0"),
            ("0\"%\""),
            ("\"0\""),
            ("#,0\""),
            (";;;"),
            ("%"),
            ("Standard"),
            ("Percent"),
            ("Currency"),
        };

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task No_measure_format_rule_touches_a_format_outside_the_plain_number_grammar()
        {
            // One model, a measure per format (the old test built the model and scanned it again for every row).
            var cases = OutsideGrammarCases;
            using var f = await NorthstarAsync();
            for (var i = 0; i < cases.Length; i++) await f.Engine.CreateMeasureAsync("table:Sales", "Case " + i, "SUM ( Sales[Amount] )", "human");
            await Mutate(f, m => { for (var i = 0; i < cases.Length; i++) m.Tables["Sales"].Measures["Case " + i].FormatString = cases[i]; });

            var rules = FormatRules();
            var card = await f.Engine.BpaScanAsync();
            for (var i = 0; i < cases.Length; i++)
            {
                var hits = card.Violations.Where(v => v.ObjectRef == "measure:Sales/Case " + i && rules.Contains(v.RuleId)).Select(v => v.RuleId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                Assert.True(hits.Length == 0,
                    $"\"{cases[i]}\" is not a plain number format, yet a rule whose fix overwrites the format string raises it: {string.Join(", ", hits)}");
            }

            for (var i = 0; i < cases.Length; i++) await PressEverythingOnceAsync(f, "measure:Sales/Case " + i);
            await f.Engine.BpaFixAllAsync("human");
            var now = await f.Sessions.Require().ReadAsync(m => cases.Select((c, i) => m.Tables["Sales"].Measures["Case " + i].FormatString).ToArray());
            for (var i = 0; i < cases.Length; i++) Assert.True(cases[i] == now[i], $"\"{cases[i]}\" was rewritten to \"{now[i]}\"");
        }

        /// <summary>V1c (D1, the static half). A measure whose format is a format string expression is shown with that
        /// expression, whatever its static FormatString says. The wrapper clears the static one when the expression is
        /// set, but a .bim can still carry both (round 6 saved exactly that), so a stale "0.00" or "0%" beside a
        /// dynamic date format must not be read as a plain number either: neither rule raises it, and no press writes
        /// a static format onto it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("0.00")]
        [InlineData("0%")]
        public async Task A_measure_with_a_dynamic_format_is_left_alone_even_beside_a_stale_static_one(string staleStatic)
        {
            using var f = await NorthstarAsync();
            const string Ref = "measure:Sales/Net Sales";
            const string Dynamic = "\"dd/mm/yyyy\"";
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Net Sales"].FormatStringExpression = Dynamic;
                m.Tables["Sales"].Measures["Net Sales"].FormatString = staleStatic;
            });
            var (fs0, fse0) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Net Sales"].FormatString, m.Tables["Sales"].Measures["Net Sales"].FormatStringExpression));
            Assert.Equal((staleStatic, Dynamic), (fs0, fse0));   // the setup really carries both

            var hits = await WritersFlaggingAsync(f, Ref);
            Assert.True(hits.Length == 0,
                $"a measure shown through a dynamic format is raised for its stale static \"{staleStatic}\": {string.Join(", ", hits)}");

            await PressEverythingAsync(f, Ref);
            var (fs, fse) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Net Sales"].FormatString, m.Tables["Sales"].Measures["Net Sales"].FormatStringExpression));
            Assert.Equal(staleStatic, fs);
            Assert.Equal(Dynamic, fse);
        }

        /// <summary>V1c, the control, and the verifier's probe P2 list, under the round-8 ruling (28 Sep). The grammar
        /// must not switch either rule off: every plain number format that is not the house style is still raised by
        /// INTEGER_FORMATTING alone, every plain percentage is still raised by PERCENTAGE_FORMATTING alone, and neither
        /// house style is raised. What changed is the fix: only a BLANK format on a value the engine proves is a whole
        /// number is filled (THE FORMAT FILL RULE, 28 Sep), and "Net Sales" is a SUM of a decimal column, so here even its
        /// blank rows are advice. Every finding is advice with no one-click fix, and a press on it, then Fix all, leaves the format
        /// exactly as the person wrote it. That the integer rule never raises a percentage is pinned here too, because
        /// the two rules must never both claim one measure. Since the Plan statement check (28 Sep) "#,##0" is not raised
        /// either: it already shows thousands separators, like "#,0" (PlanStatementRuleClashTests).</summary>
        private static readonly (string Existing, string ExpectedRule, string AfterPress)[] StillAdviseCases =
        {
            ("", "INTEGER_FORMATTING", ""),
            ("   ", "INTEGER_FORMATTING", "   "),
            ("0", "INTEGER_FORMATTING", "0"),
            ("0.00", "INTEGER_FORMATTING", "0.00"),
            ("#,##0", null, "#,##0"),
            ("#,##0.00", "INTEGER_FORMATTING", "#,##0.00"),
            ("#,0.00", "INTEGER_FORMATTING", "#,0.00"),
            ("0.0", "INTEGER_FORMATTING", "0.0"),
            ("#", "INTEGER_FORMATTING", "#"),
            ("##0", "INTEGER_FORMATTING", "##0"),
            ("#,0.000", "INTEGER_FORMATTING", "#,0.000"),
            ("#,##0;-#,##0", "INTEGER_FORMATTING", "#,##0;-#,##0"),
            ("0;(0)", "INTEGER_FORMATTING", "0;(0)"),
            ("#,0;(#,0)", "INTEGER_FORMATTING", "#,0;(#,0)"),
            ("#,0;-#,0;\"-\"", "INTEGER_FORMATTING", "#,0;-#,0;\"-\""),
            ("00000", "INTEGER_FORMATTING", "00000"),
            ("#,0", null, "#,0"),
            ("#,0.0", null, "#,0.0"),
            ("0%", "PERCENTAGE_FORMATTING", "0%"),
            ("0.0%", "PERCENTAGE_FORMATTING", "0.0%"),
            ("0.00%", "PERCENTAGE_FORMATTING", "0.00%"),
            ("#,0%", "PERCENTAGE_FORMATTING", "#,0%"),
            ("#,##0.0%", "PERCENTAGE_FORMATTING", "#,##0.0%"),
            ("0.00%;-0.00%;0.00%", "PERCENTAGE_FORMATTING", "0.00%;-0.00%;0.00%"),
            ("#,0.0%", "PERCENTAGE_FORMATTING", "#,0.0%"),
            ("0 %", "PERCENTAGE_FORMATTING", "0 %"),
            ("0%;-0%;\"-\"", "PERCENTAGE_FORMATTING", "0%;-0%;\"-\""),
            ("#,0.0%;-#,0.0%;#,0.0%", null, "#,0.0%;-#,0.0%;#,0.0%"),
        };

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Both_measure_format_rules_still_advise_every_plain_number_and_percentage()
        {
            // One model, a measure per format (the old test built the model and scanned it again for every row).
            var cases = StillAdviseCases;
            using var f = await NorthstarAsync();
            for (var i = 0; i < cases.Length; i++) await f.Engine.CreateMeasureAsync("table:Sales", "Case " + i, "SUM ( Sales[Amount] )", "human");
            await Mutate(f, m => { for (var i = 0; i < cases.Length; i++) m.Tables["Sales"].Measures["Case " + i].FormatString = cases[i].Existing; });

            var rules = FormatRules();
            void AssertFindings(BpaScorecard card, string when)
            {
                for (var i = 0; i < cases.Length; i++)
                {
                    var got = card.Violations.Where(v => v.ObjectRef == "measure:Sales/Case " + i && rules.Contains(v.RuleId))
                        .Select(v => (v.RuleId, v.CanAutoFix)).OrderBy(x => x.RuleId, StringComparer.Ordinal).ToArray();
                    var want = cases[i].ExpectedRule == null ? Array.Empty<(string, bool)>() : new[] { (cases[i].ExpectedRule, false) };
                    Assert.True(want.SequenceEqual(got), $"\"{cases[i].Existing}\" {when}: expected {string.Join(",", want)} got {string.Join(",", got)}");
                }
            }
            AssertFindings(await f.Engine.BpaScanAsync(), "first read");
            for (var i = 0; i < cases.Length; i++)
                if (cases[i].ExpectedRule != null)
                    Assert.Contains("only fills a blank format string",
                        (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.BpaFixAsync(cases[i].ExpectedRule, "measure:Sales/Case " + i, "agent"))).Message,
                        StringComparison.Ordinal);
            await f.Engine.BpaFixAllAsync("human");
            var now = await f.Sessions.Require().ReadAsync(m => cases.Select((c, i) => m.Tables["Sales"].Measures["Case " + i].FormatString).ToArray());
            for (var i = 0; i < cases.Length; i++) Assert.True(cases[i].AfterPress == now[i], $"\"{cases[i].Existing}\" became \"{now[i]}\" after Fix all, expected \"{cases[i].AfterPress}\"");
            // Advice stays advice.
            AssertFindings(await f.Engine.BpaScanAsync(), "after Fix all");
        }

        /// <summary>Round 8, the coordinator's ruling (28 Sep): a fix NEVER writes over a format string that is
        /// already there, on any door, and only fills a blank one on an object with no format string expression. Eight
        /// rounds each found a format a grammar did not know (a date, a currency, "##0,," shown in millions, "0.00;;"
        /// hiding values, a digit mask, a dynamic format), so the rule is enforced once, in the analyzer, for every rule
        /// whose fix targets FormatString, a custom one included. Proven on both doors, Pro and Free: nothing offers the
        /// fix, one press refuses in words, Fix all and apply_safe_fixes leave every format as written, the plan seeds
        /// nothing, the fix prompt sends the assistant to the tools a person uses, the advice never blocks the deploy
        /// gate, and a .bim saved and opened in a new engine reads the same. The blank count is still filled.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("pro")]
        public Task No_fix_writes_over_a_format_string_on_any_door(string tier) => No_fix_writes_over_a_format_string_on_any_doorBodyAsync(tier);

        [Theory]
        [Trait(TestCost.Key, TestCost.Sweep)]
        [InlineData("free")]
        public Task No_fix_writes_over_a_format_string_on_any_door_on_free(string tier) => No_fix_writes_over_a_format_string_on_any_doorBodyAsync(tier);

        private async Task No_fix_writes_over_a_format_string_on_any_doorBodyAsync(string tier)
        {
            using var f = new DecisionFixture(tier == "pro" ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Formats", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            var formats = new Dictionary<string, string>
            {
                ["Decimals"] = "0.00", ["Rate"] = "0%", ["Millions"] = "##0,,", ["Hidden Negatives"] = "0.00;;",
                ["Date Key"] = "0000-00-00", ["Written"] = "#,##0.00;(#,##0.00)",
            };
            foreach (var name in formats.Keys.Concat(new[] { "Dynamic" }))
                await e.CreateMeasureAsync("table:Sales", name, "SUM ( Sales[Amount] )", "human");
            // The fillable control is a count: a blank format is filled only on a proven whole number (THE FORMAT FILL RULE).
            await e.CreateMeasureAsync("table:Sales", "Blank", "COUNTROWS ( Sales )", "human");
            const string Dynamic = "\"dd/mm/yyyy\"";
            await Mutate(f, m =>
            {
                foreach (var kv in formats) m.Tables["Sales"].Measures[kv.Key].FormatString = kv.Value;
                m.Tables["Sales"].Measures["Dynamic"].FormatStringExpression = Dynamic;
            });
            // A custom rule whose fix writes a format onto every measure but the blank one: the analyzer's rule, not
            // the shipped rules' expressions, is what keeps it off a format that is already there.
            await e.LoadBpaRulesAsync("[{\"ID\":\"TEST_HOUSE_FORMAT\",\"Name\":\"House format\",\"Category\":\"Formatting\","
                + "\"Severity\":3,\"Scope\":\"Measure\",\"Expression\":\"Name <> \\\"Blank\\\"\","
                + "\"FixExpression\":\"FormatString = \\\"#,0.00\\\"\"}]", replace: true, "human");
            var rpc = new EngineRpcTarget(e);

            async Task<Dictionary<string, (string fs, string fse)>> ReadAll(SessionManager s) =>
                await s.Require().ReadAsync(m => m.Tables["Sales"].Measures.ToDictionary(x => x.Name, x => (x.FormatString ?? "", x.FormatStringExpression ?? "")));
            void AssertUnchanged(Dictionary<string, (string fs, string fse)> now)
            {
                foreach (var kv in formats) Assert.Equal((kv.Value, ""), now[kv.Key]);
                Assert.Equal(("", Dynamic), now["Dynamic"]);
            }
            static string[] FormatWriters(BpaScorecard c) => c.Violations
                .Where(v => v.CanAutoFix && (v.RuleId == "INTEGER_FORMATTING" || v.RuleId == "PERCENTAGE_FORMATTING" || v.RuleId == "TEST_HOUSE_FORMAT"))
                .Select(v => v.RuleId + " on " + v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray();

            // 1. The scan: the only finding that offers a format fix is the integer rule on the blank measure. Advice
            // is still raised, and the custom rule still raises every measure it names.
            var card = await e.BpaScanAsync();
            Assert.Equal(new[] { "INTEGER_FORMATTING on measure:Sales/Blank" }, FormatWriters(card));
            foreach (var name in new[] { "Decimals", "Millions", "Hidden Negatives", "Date Key", "Written" })
                Assert.Contains(card.Violations, v => v.RuleId == "INTEGER_FORMATTING" && v.ObjectRef == "measure:Sales/" + name && !v.CanAutoFix);
            Assert.Contains(card.Violations, v => v.RuleId == "PERCENTAGE_FORMATTING" && v.ObjectRef == "measure:Sales/Rate" && !v.CanAutoFix);
            Assert.Equal(7, card.Violations.Count(v => v.RuleId == "TEST_HOUSE_FORMAT" && !v.CanAutoFix));

            // 2. One press, on both doors, is refused in words for every rule and every measure that has a format.
            foreach (var name in formats.Keys.Concat(new[] { "Dynamic" }))
                foreach (var rule in new[] { "INTEGER_FORMATTING", "PERCENTAGE_FORMATTING", "TEST_HOUSE_FORMAT" })
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ApplyFix(e, "bpa", rule, "measure:Sales/" + name));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.bpaFix(rule, "measure:Sales/" + name));
                }
            var words = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ApplyFix(e, "bpa", "TEST_HOUSE_FORMAT", "measure:Sales/Dynamic"));
            Assert.Contains("format string expression", words.Message, StringComparison.Ordinal);
            AssertUnchanged(await ReadAll(f.Sessions));

            // 3. The plan seeds no format fix but the fill, on both doors, and the fix prompt names the person's tools.
            foreach (var plan in new[] { await McpTools.ProposePlan(e, null, false, 0), await rpc.proposePlan(null, false, 0) })
                Assert.Equal(new[] { "INTEGER_FORMATTING|measure:Sales/Blank" },
                    plan.Items.Where(i => i.Kind == "bpa_fix" && i.Target == "formatstring").Select(i => i.RuleId + "|" + i.ObjectRef).ToArray());
            foreach (var prompt in new[] { await McpTools.GetFixPrompt(e, "bpa", "INTEGER_FORMATTING", "measure:Sales/Millions"),
                                           await rpc.bpaGetFixPrompt("INTEGER_FORMATTING", "measure:Sales/Millions") })
            {
                // Advice, not a fault: the assistant is told what the format is and to let the person decide.
                Assert.Equal("(advice)", prompt.Tool);
                Assert.DoesNotContain("call apply_fix", prompt.Prompt, StringComparison.Ordinal);
                Assert.Contains("It is advice, not a fault to fix", prompt.Prompt, StringComparison.Ordinal);
                Assert.Contains("already set (\"##0,,\")", prompt.Prompt, StringComparison.Ordinal);
                Assert.Contains("change it only if they agree", prompt.Prompt, StringComparison.Ordinal);
            }

            // 4. The advice is severity 1, so it never blocks the deploy gate (its own test below measures the gate).
            Assert.DoesNotContain(card.Violations, v => (v.RuleId == "INTEGER_FORMATTING" || v.RuleId == "PERCENTAGE_FORMATTING") && v.Severity >= 2);

            // 5. Fix all on the Studio door and apply_safe_fixes on the MCP door leave every format as written and
            // fill the blank measure, once.
            await rpc.bpaFixAll();
            Assert.IsType<BpaFixAllResult>(await McpTools.ApplySafeFixes(e, "bpa"));
            var after = await ReadAll(f.Sessions);
            AssertUnchanged(after);
            Assert.Equal(("#,0", ""), after["Blank"]);

            // 6. A .bim saved and opened in a new engine carries the same formats and raises the same findings.
            var path = Path.Combine(f.Workspace, "Formats.bim");
            await e.SaveAsync(path, "bim", false, "human");
            Assert.DoesNotContain("\"formatString\": \"#,0.00\"", File.ReadAllText(path), StringComparison.Ordinal);
            using var g = new DecisionFixture(tier == "pro" ? TestEntitlements.Pro : TestEntitlements.Free);
            await g.Engine.OpenAsync(path);
            AssertUnchanged(await ReadAll(g.Sessions));
            var reopened = await g.Engine.BpaScanAsync();
            Assert.Empty(FormatWriters(reopened));
            Assert.Equal(
                card.Violations.Where(v => v.RuleId != "INTEGER_FORMATTING" || v.ObjectRef != "measure:Sales/Blank")
                    .Where(v => v.RuleId.EndsWith("_FORMATTING", StringComparison.Ordinal) || v.RuleId == "TEST_HOUSE_FORMAT")
                    .Select(v => v.RuleId + "|" + v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                reopened.Violations.Where(v => v.RuleId.EndsWith("_FORMATTING", StringComparison.Ordinal) || v.RuleId == "TEST_HOUSE_FORMAT")
                    .Select(v => v.RuleId + "|" + v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        /// <summary>Round 8, the plan door's second answer. The verifier's F4 case is a rule that stops raising the
        /// measure once it has a dynamic format, so the plan skips the item because the rule no longer asks for it. A
        /// rule that STILL raises the measure after that change (a custom one here) is the other half: its fix is no
        /// longer the fill of a blank format, so the plan skips it for that reason, and nothing lands beside the
        /// dynamic format. Both doors run the same executor; this pins the reason, the round-8 file pins both doors.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_planned_format_fix_is_skipped_when_the_rule_still_raises_a_measure_that_now_has_a_dynamic_format()
        {
            using var f = await NorthstarAsync();
            // The custom fill must be one THE FORMAT FILL RULE allows (a whole-number format on a count) for the plan to
            // seed it at all, and the integer rule would claim the same object and target, so a model rule switches it off.
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Rows", "COUNTROWS ( Sales )", "human");
            await f.Engine.LoadBpaRulesAsync("[{\"ID\":\"TEST_HOUSE_FORMAT\",\"Name\":\"House format\",\"Category\":\"Formatting\","
                + "\"Severity\":3,\"Scope\":\"Measure\",\"Expression\":\"Name = \\\"Order Rows\\\"\","
                + "\"FixExpression\":\"FormatString = \\\"#,0\\\"\"},"
                + "{\"ID\":\"INTEGER_FORMATTING\",\"Name\":\"off\",\"Category\":\"Formatting\",\"Severity\":1,\"Scope\":\"Measure\",\"Expression\":\"false\"}]", replace: true, "human");
            var plan = await f.Engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "human");
            var item = Assert.Single(plan.Items, i => i.Kind == "bpa_fix" && i.RuleId == "TEST_HOUSE_FORMAT");

            await Mutate(f, m => m.Tables["Sales"].Measures["Order Rows"].FormatStringExpression = "\"0.0%\"");
            Assert.Contains((await f.Engine.BpaScanAsync()).Violations, v => v.RuleId == "TEST_HOUSE_FORMAT" && !v.CanAutoFix);

            var report = await f.Engine.ApplyPlanAsync(new[] { item.Id }, "human");
            var done = report.Items.Single(i => i.Id == item.Id);
            Assert.Equal("skipped", done.Status);
            Assert.Contains("format string expression", done.Note, StringComparison.Ordinal);
            var (fs, fse) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Order Rows"].FormatString, m.Tables["Sales"].Measures["Order Rows"].FormatStringExpression));
            Assert.True(string.IsNullOrEmpty(fs), $"the plan wrote \"{fs}\" beside the dynamic format");
            Assert.Equal("\"0.0%\"", fse);
        }

        /// <summary>Round 8. The deploy gate blocks on a severity-2 finding that has no automatic fix. Before round 8
        /// neither measure format rule could block, because its fix was always offered; now a format that is already
        /// there is advice with no fix, so at severity 2 every model with a 0.00 or 0.0% measure could not ship. Both
        /// rules are severity 1, and the gate is measured, not inferred: its blocking count is the same whether two
        /// measures carry advice formats or the house styles that clear them.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_format_advice_never_blocks_the_deploy_gate()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "Decimals", "SUM ( Sales[Amount] ) * 2", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Rate", "DIVIDE ( SUM ( Sales[Amount] ), 3 )", "human");
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Decimals"].FormatString = "0.00";
                m.Tables["Sales"].Measures["Rate"].FormatString = "0%";
            });
            var card = await f.Engine.BpaScanAsync();
            Assert.Contains(card.Violations, v => v.RuleId == "INTEGER_FORMATTING" && v.ObjectRef == "measure:Sales/Decimals" && !v.CanAutoFix);
            Assert.Contains(card.Violations, v => v.RuleId == "PERCENTAGE_FORMATTING" && v.ObjectRef == "measure:Sales/Rate" && !v.CanAutoFix);
            var advised = await f.Engine.DeployGateAsync(null, "human");

            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Decimals"].FormatString = "#,0";
                m.Tables["Sales"].Measures["Rate"].FormatString = "#,0.0%;-#,0.0%;#,0.0%";
            });
            Assert.DoesNotContain((await f.Engine.BpaScanAsync()).Violations, v => v.RuleId is "INTEGER_FORMATTING" or "PERCENTAGE_FORMATTING"
                && (v.ObjectRef == "measure:Sales/Decimals" || v.ObjectRef == "measure:Sales/Rate"));
            var cleared = await f.Engine.DeployGateAsync(null, "human");

            Assert.Equal(cleared.BpaBlocking, advised.BpaBlocking);
            Assert.Equal(cleared.Pass, advised.Pass);
        }

        /// <summary>Round 8, the advisor's H1: the change-plan door has a second format writer, FMT-MEASURE's
        /// set_measure_format item, which the assistant fills in and the person approves. Its stale-value guard reads the
        /// static format only, so a measure given a format string expression between proposing and applying still read
        /// as blank, and the static format was saved beside the dynamic one (F4's harm, by the readiness half of the
        /// door). It is now skipped with a note that says why.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_planned_measure_format_is_skipped_when_the_measure_now_has_a_dynamic_format()
        {
            using var f = await NorthstarAsync();
            // The plan keeps one item per object and target, and the integer rule's blank fill would take this one, so
            // a model rule switches that rule off here and the readiness item is the one seeded.
            await f.Engine.LoadBpaRulesAsync("[{\"ID\":\"INTEGER_FORMATTING\",\"Name\":\"off\",\"Category\":\"Formatting\","
                + "\"Severity\":1,\"Scope\":\"Measure\",\"Expression\":\"false\"}]", replace: true, "human");
            var plan = await f.Engine.ProposePlanAsync(null, includeAi: true, maxAiItems: 40, "human");
            var item = Assert.Single(plan.Items, i => i.Kind == "set_measure_format" && i.ObjectRef == "measure:Sales/Net Sales");
            await f.Engine.SetPlanItemAsync(item.Id, "#,0", true, "human");

            await Mutate(f, m => m.Tables["Sales"].Measures["Net Sales"].FormatStringExpression = "\"dd/mm/yyyy\"");

            var report = await f.Engine.ApplyPlanAsync(new[] { item.Id }, "human");
            var done = report.Items.Single(i => i.Id == item.Id);
            Assert.Equal("skipped", done.Status);
            Assert.Contains("format string expression", done.Note, StringComparison.Ordinal);
            var (fs, fse) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Net Sales"].FormatString, m.Tables["Sales"].Measures["Net Sales"].FormatStringExpression));
            Assert.True(string.IsNullOrEmpty(fs), $"the plan wrote \"{fs}\" beside the dynamic format");
            Assert.Equal("\"dd/mm/yyyy\"", fse);
        }

        /// <summary>Round 8, the advisor's M1. The plan's re-check at apply time must answer what apply_fix answers: a
        /// fix that has already landed (here by Fix all, between proposing and applying) is a quiet applied no-op, not a
        /// skip. A skip would make an approved Model Plan set roll back whole for a change that is already in place.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_planned_fix_that_already_landed_still_reads_as_applied()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Rows", "COUNTROWS ( Sales )", "human");   // a fill the rule allows
            var plan = await f.Engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "human");
            var item = Assert.Single(plan.Items, i => i.Kind == "bpa_fix" && i.RuleId == "INTEGER_FORMATTING" && i.ObjectRef == "measure:Sales/Order Rows");

            await f.Engine.BpaFixAllAsync("human");
            Assert.Equal("#,0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Order Rows"].FormatString));

            var report = await f.Engine.ApplyPlanAsync(new[] { item.Id }, "human");
            Assert.Equal("applied", report.Items.Single(i => i.Id == item.Id).Status);
            Assert.Equal("#,0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Order Rows"].FormatString));
        }

        /// <summary>Round 8, the advisor's LOW, narrowed by THE FORMAT FILL RULE (28 Sep). The rule is about a format,
        /// whichever half a fix targets. A custom fix that sets FormatStringExpression must not replace a dynamic format,
        /// must not set one beside a static format (the wrapper then clears the static one, which is a write over it),
        /// and since the fill rule it does not fill a measure with no format either: what an expression shows cannot be
        /// proved. Fix all, and a press on each door, leave all three alone.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_fix_that_sets_a_format_string_expression_is_never_automatic()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "Static", "SUM ( Sales[Amount] ) * 2", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Dynamic", "SUM ( Sales[Amount] ) * 3", "human");
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Static"].FormatString = "0.00";
                m.Tables["Sales"].Measures["Dynamic"].FormatStringExpression = "\"dd/mm/yyyy\"";
            });
            await f.Engine.LoadBpaRulesAsync("[{\"ID\":\"TEST_DYNAMIC_FORMAT\",\"Name\":\"Dynamic format\",\"Category\":\"Formatting\","
                + "\"Severity\":3,\"Scope\":\"Measure\",\"Expression\":\"true\","
                + "\"FixExpression\":\"FormatStringExpression = \\\"SELECTEDMEASUREFORMATSTRING ()\\\"\"}]", replace: true, "human");
            var rpc = new EngineRpcTarget(f.Engine);

            var card = await f.Engine.BpaScanAsync();
            Assert.Empty(card.Violations.Where(v => v.RuleId == "TEST_DYNAMIC_FORMAT" && v.CanAutoFix));
            Assert.All(card.Violations.Where(v => v.RuleId == "TEST_DYNAMIC_FORMAT"), v => Assert.True(v.Advice));
            foreach (var name in new[] { "Static", "Dynamic", "Net Sales" })
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ApplyFix(f.Engine, "bpa", "TEST_DYNAMIC_FORMAT", "measure:Sales/" + name));
                await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.bpaFix("TEST_DYNAMIC_FORMAT", "measure:Sales/" + name));
            }
            await f.Engine.BpaFixAllAsync("human");

            var now = await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures.ToDictionary(x => x.Name, x => (x.FormatString ?? "", x.FormatStringExpression ?? "")));
            Assert.Equal(("0.00", ""), now["Static"]);
            Assert.Equal(("", "\"dd/mm/yyyy\""), now["Dynamic"]);
            Assert.Equal(("", ""), now["Net Sales"]);
        }

        /// <summary>V1c (D4). The flag rule's second branch catches any column whose name ends in " Flag" and is not
        /// text, so a date column named "Renewal Flag" was raised and told to show itself as Yes or No, which a date
        /// cannot be. A date column is never raised for its name, with or without a format string, and a number flag
        /// named the same way still is.</summary>
        [Fact]
        public async Task The_flag_rule_never_raises_a_date_column_for_its_name()
        {
            using var f = await NorthstarAsync();
            const string Rule = "FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS";
            await f.Engine.CreateColumnAsync("table:Sales", "Renewal Flag", "DateTime", "RenewalFlag", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Return Flag", "Int64", "ReturnFlag", "human");

            var flagged = (await f.Engine.BpaScanAsync()).Violations
                .Where(v => v.RuleId == Rule).Select(v => v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "column:Sales/Return Flag" }, flagged);
        }

        /// <summary>V1c, the copy. Each rule's Description is the message a person reads for its finding, so it must say
        /// what the expression does and must no longer carry the promises rounds 5 and 6 broke ("Any format string
        /// carrying a letter is left alone", "Currency formats are left alone" as a claim resting on the dollar sign).</summary>
        [Fact]
        public void The_format_rule_descriptions_say_what_the_expressions_do()
        {
            var rules = BpaRuleSet.Standard().ToDictionary(r => r.ID);
            foreach (var id in new[] { "INTEGER_FORMATTING", "PERCENTAGE_FORMATTING" })
            {
                var d = rules[id].Description;
                Assert.Contains("format string expression", d, StringComparison.Ordinal);
                Assert.Contains("in any language or script", d, StringComparison.Ordinal);
                Assert.Contains("currency symbol", d, StringComparison.Ordinal);
                Assert.DoesNotContain("carrying a letter", d, StringComparison.Ordinal);
                Assert.DoesNotContain("no letter anywhere", d, StringComparison.Ordinal);
                Assert.Contains("FormatStringExpression", rules[id].Expression, StringComparison.Ordinal);
                // Round 8: advice on a format that is already there, and the fix never writes over one.
                Assert.Contains("advice only and has no one-click fix", d, StringComparison.Ordinal);
                Assert.Contains("a fix never writes over a format string that is already there", d, StringComparison.Ordinal);
                Assert.Contains("any number of quoted pieces", d, StringComparison.Ordinal);
                Assert.DoesNotContain("at most a quoted piece", d, StringComparison.Ordinal);
                Assert.Equal(1, rules[id].Severity);   // advice: at 2 the deploy gate would block on a finding with no fix
                Assert.DoesNotContain("A-Za-z", rules[id].Expression, StringComparison.Ordinal);
            }
            Assert.Contains("date column is never flagged for its name", rules["FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS"].Description, StringComparison.Ordinal);
            Assert.Contains("format string is blank (empty, or only spaces or other blank characters)", rules["INTEGER_FORMATTING"].Description, StringComparison.Ordinal);
            Assert.Contains("Its one-click fix fills in #,0", rules["INTEGER_FORMATTING"].Description, StringComparison.Ordinal);
            // THE FORMAT FILL RULE (28 Sep): the fill is promised only where it is proved, and the date rules are advice.
            Assert.Contains("only when Semanticus can prove the measure holds a whole number", rules["INTEGER_FORMATTING"].Description, StringComparison.Ordinal);
            Assert.Contains("a ratio such as 73% would read 1", rules["INTEGER_FORMATTING"].Description, StringComparison.Ordinal);
            foreach (var id in new[] { "DATECOLUMN_FORMATSTRING", "MONTHCOLUMN_FORMATSTRING" })
            {
                Assert.Contains("advice only and has no one-click fix", rules[id].Description, StringComparison.Ordinal);
                Assert.DoesNotContain("The one-click fix writes", rules[id].Description, StringComparison.Ordinal);
            }
        }

        /// <summary>V1b, the guard rail rather than the single case, and the whole-file sibling of the column test
        /// above. EVERY shipped rule whose fix writes FormatString, at ANY scope, must leave alone a format string
        /// with a letter in it. That is the one shape a numeric house style can never prove is its own, and it is
        /// where every real date and time format lives: Long Date, General Date, hh:nn:ss and dd/mm/yyyy alike. A
        /// fourth rule of that shape added later, or any of the four loosened again, fails here on both a measure
        /// and a column.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("Long Date")]
        [InlineData("Long Time")]
        [InlineData("dd/mm/yyyy hh:nn")]
        [InlineData("mmm yyyy")]
        public async Task No_shipped_format_rule_writes_over_a_format_string_with_a_letter_in_it(string existing)
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "Last Order Date", "MAX ( Sales[Amount] )", "human");
            await f.Engine.CreateColumnAsync("table:Date", "Order Date", "DateTime", "OrderDate", "human");
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Last Order Date"].FormatString = existing;
                m.Tables["Date"].Columns["Order Date"].FormatString = existing;
            });

            var writers = BpaRuleSet.Standard()
                .Where(r => (r.FixExpression ?? "").Replace(" ", "").StartsWith("FormatString=", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.ID).ToArray();
            Assert.Equal(4, writers.Length);   // three would mean one was dropped, five that one arrived unnoticed

            var refs = new[] { "measure:Sales/Last Order Date", "column:Date/Order Date" };
            var hits = (await f.Engine.BpaScanAsync()).Violations
                .Where(v => refs.Contains(v.ObjectRef) && writers.Contains(v.RuleId))
                .Select(v => v.RuleId + " on " + v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.True(hits.Length == 0,
                $"\"{existing}\" is raised by a rule whose fix overwrites the format string: {string.Join(", ", hits)}");

            foreach (var rule in writers)
                foreach (var objRef in refs)
                    try { await f.Engine.BpaFixAsync(rule, objRef, "agent"); }
                    catch (InvalidOperationException) { /* refusing the press is the point */ }

            var (measure, column) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Last Order Date"].FormatString, m.Tables["Date"].Columns["Order Date"].FormatString));
            Assert.Equal(existing, measure);
            Assert.Equal(existing, column);
        }

        /// <summary>V2. The flag rule's description named one remedy, the three-part Yes/No format string, and the
        /// clearing clause required Int64, so a true/false flag was flagged permanently and the remedy its own
        /// message named did nothing. Every flag the rule catches now clears the same way. D1 is not loosened by
        /// that: the two-part form still fails for a true/false flag exactly as it does for a whole-number one,
        /// because its first part covers zero (false) as well as positive (true).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("Boolean")]
        [InlineData("Int64")]
        public async Task Every_flag_the_rule_catches_clears_with_the_format_string_its_message_names(string dataType)
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Sales", "Return Flag", dataType, "ReturnFlag", "human");
            const string Rule = "FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS";
            bool Flagged(BpaScorecard r) => r.Violations.Any(x => x.RuleId == Rule && x.ObjectRef == "column:Sales/Return Flag");

            Assert.True(Flagged(await f.Engine.BpaScanAsync()), $"a {dataType} flag column is not flagged at all");

            await Mutate(f, m => m.Tables["Sales"].Columns["Return Flag"].FormatString = "\"Yes\";\"No\"");
            Assert.True(Flagged(await f.Engine.BpaScanAsync()), "the two-part form shows a false flag as Yes, so it must not clear the rule");

            await Mutate(f, m => m.Tables["Sales"].Columns["Return Flag"].FormatString = "\"Yes\";\"Yes\";\"No\"");
            Assert.False(Flagged(await f.Engine.BpaScanAsync()), "the format string the rule's own message names did not clear it");

            // And the message a person reads promises exactly that, for every type it catches.
            var desc = BpaRuleSet.Standard().Single(r => r.ID == Rule).Description;
            Assert.Contains("true/false flag clears it the same way", desc, StringComparison.Ordinal);
            Assert.DoesNotContain("An integer flag clears", desc, StringComparison.Ordinal);
        }

        /// <summary>V3. The skip reason told a person to propose the removal twice: the verdict sentence already ends
        /// with that advice and the tail said it again in different words, which read as two different instructions.
        /// The tail now only names the calls that carry the advice out, so the reason says it once and still tells
        /// the assistant exactly which route to take.</summary>
        [Fact]
        public async Task The_visible_attribute_skip_reason_says_it_once_and_still_names_the_route()
        {
            using var f = await NorthstarAsync();

            var report = await f.Engine.RemoveSafeObjectsAsync(new[] { "column:Customer/City" }, null, "agent");
            var reason = Assert.Single(report.Skipped, k => k.Ref == "column:Customer/City").Reason;

            Assert.Equal(1, Regex.Matches(reason, "propose", RegexOptions.IgnoreCase).Count);
            Assert.Contains("propose its removal and approve that change", reason, StringComparison.Ordinal);
            Assert.Contains("add_plan_item", reason, StringComparison.Ordinal);
            Assert.Contains("delete_object", reason, StringComparison.Ordinal);
        }

        /// <summary>V4. The cleanup machinery refuses a calculation group's own column on both doors; the per-object
        /// door did not, so delete_object left a group that exists and that nothing can pick a calculation from, and
        /// the model saved that way. A person's explicit removal is honoured wherever the model stays valid, and this
        /// is the case where it does not. The refusal sits at the one method every delete path funnels through, so
        /// both doors say the same sentence, and it names the removal that IS allowed: the whole group.</summary>
        [Fact]
        public async Task Delete_object_refuses_a_calculation_groups_own_column_on_both_doors()
        {
            using var f = await NorthstarAsync();
            var cg = await f.Engine.CreateCalculationGroupAsync("Time Intelligence", "human");
            await f.Engine.CreateCalculationItemAsync(cg, "Current", "SELECTEDMEASURE ()", "human");
            var pickerRef = await f.Sessions.Require().ReadAsync(m =>
                ObjectRefs.For(((CalculationGroupTable)m.Tables["Time Intelligence"]).Columns.First(c => !c.IsHidden)));
            var rpc = new EngineRpcTarget(f.Engine);   // the door Studio calls

            foreach (var refuse in new Func<Task>[]
            {
                () => f.Engine.DeleteObjectAsync(pickerRef, "human"),                         // MCP door / delete_object
                () => rpc.deleteObject(pickerRef),                                            // Studio door
                () => f.Engine.DeleteObjectsAsync(new[] { pickerRef, "column:Date/Year" }, "human"),   // multi-select
            })
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(refuse);
                Assert.Contains("calculation group 'Time Intelligence'", ex.Message, StringComparison.Ordinal);
                Assert.Contains("Delete the whole calculation group", ex.Message, StringComparison.Ordinal);
            }

            // The group is still pickable, and the batch that was refused wrote nothing at all.
            Assert.True(0 < await f.Sessions.Require().ReadAsync(m =>
                ((CalculationGroupTable)m.Tables["Time Intelligence"]).Columns.Count(c => !c.IsHidden)));
            Assert.NotNull(await f.Sessions.Require().ReadAsync(m => ObjectRefs.Resolve(m, "column:Date/Year")));

            // The removal the message offers instead is allowed, and it undoes in one step.
            Assert.True((await f.Engine.DeleteObjectAsync("table:Time Intelligence", "human")).Changed);
            Assert.Null(await f.Sessions.Require().ReadAsync(m => ObjectRefs.Resolve(m, "table:Time Intelligence")));
            await f.Engine.UndoAsync("human");
            Assert.NotNull(await f.Sessions.Require().ReadAsync(m => ObjectRefs.Resolve(m, pickerRef)));
        }

        // ---- THE FORMAT FILL RULE (coordinator, 28 Sep): the one "may fill" check -------------------------------

        /// <summary>FormatFill.MayFill is the single answer every automatic format write asks, and the m11/plan-rows
        /// lane calls it for the Plan's own rows. A blank format may be filled with a whole-number format only when the
        /// engine PROVES the value is a whole number: a count, or a SUM of a whole-number column the model shows is a
        /// quantity (not a key, an ID, a date key, a code or a flag; through CALCULATE or a reference to such a measure too),
        /// or such a column itself. MIN and MAX are never proved. Everything else answers false, including a word in a comment or a string, which is not a call.</summary>
        private static readonly (string Dax, bool MayFill)[] FillProvenCases =
        {
            ("COUNTROWS ( Sales )", true),
            ("COUNT ( Sales[Qty] )", true),
            ("COUNTA ( Sales[Amount] )", true),
            ("DISTINCTCOUNT ( Sales[CustomerKey] )", true),
            ("DISTINCTCOUNTNOBLANK ( Sales[CustomerKey] )", true),
            ("SUM ( Sales[Qty] )", true),
            ("MIN ( 'Sales'[Qty] )", false),
            ("MAX ( Sales[Qty] )", false),
            ("( COUNTROWS ( Sales ) )", true),
            ("CALCULATE ( COUNTROWS ( Sales ), Sales[Qty] > 2 )", true),
            ("[Rows]", true),
            ("SUM ( Sales[Amount] )", false),
            ("SUM ( Sales[Rate] )", false),
            ("MAX ( Sales[Order Date] )", false),
            ("SUM ( Sales[CustomerKey] )", false),
            ("MAX ( Sales[Year] )", false),
            ("DIVIDE ( [Rows], 3 )", false),
            ("[Rows] / 3", false),
            ("COUNTROWS ( Sales ) * 1.5", false),
            ("[Rows] + 0.5", false),
            ("2", false),
            ("AVERAGE ( Sales[Qty] )", false),
            ("SUMX ( Sales, Sales[Qty] )", false),
            ("VAR x = COUNTROWS ( Sales ) RETURN x", false),
            ("\"COUNTROWS ( Sales )\"", false),
            ("// COUNTROWS ( Sales )\n0.73", false),
            ("[Missing]", false),
            ("[Loop]", false),
        };

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Format_fill_may_fill_only_a_proven_whole_number()
        {
            // One model, one probe measure per case (the old test built the model again for every row).
            var cases = FillProvenCases;
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "fill-proven", async b =>
            {
                var e = b.Engine;
                await e.CreateModelAsync("Fill", 1604);
                await e.CreateTableAsync("Sales", "human");
                foreach (var (name, type) in new[] { ("Qty", "Int64"), ("Amount", "Decimal"), ("Rate", "Double"), ("Order Date", "DateTime"),
                                                     ("CustomerKey", "Int64"), ("Year", "Int64") })
                    await e.CreateColumnAsync("table:Sales", name, type, name.Replace(" ", ""), "human");
                await e.CreateMeasureAsync("table:Sales", "Rows", "COUNTROWS ( Sales )", "human");
                await e.CreateMeasureAsync("table:Sales", "Loop", "1", "human");
                for (var i = 0; i < cases.Length; i++) await e.CreateMeasureAsync("table:Sales", "Probe " + i, "1", "human");
                // Written through the model, not create_measure, which refuses DAX it cannot bind ([Missing], a self-reference).
                await Mutate(b, m =>
                {
                    m.Tables["Sales"].Measures["Loop"].Expression = "[Loop]";
                    for (var i = 0; i < cases.Length; i++) m.Tables["Sales"].Measures["Probe " + i].Expression = cases[i].Dax;
                });
            }, "Fill.bim");

            var read = await f.Sessions.Require().ReadAsync(m => cases.Select((c, i) =>
            {
                var probe = m.Tables["Sales"].Measures["Probe " + i];
                return (c.Dax, c.MayFill, Fills: FormatFill.MayFill(probe, "#,0"),
                        Others: new[] { FormatFill.MayFill(probe, "0.00"), FormatFill.MayFill(probe, "yyyy-mm-dd"), FormatFill.MayFill(probe, "0%") });
            }).ToArray());
            foreach (var r in read)
            {
                Assert.True(r.MayFill == r.Fills, $"{r.Dax}: expected MayFill {r.MayFill}, got {r.Fills}");
                Assert.True(r.Others.All(x => !x), $"{r.Dax}: filled with a format that is not a whole-number format");   // never with a format that is not a whole-number format
            }

            // An object that already has a format, static or dynamic, is never filled.
            await Mutate(f, m => { for (var i = 0; i < cases.Length; i++) m.Tables["Sales"].Measures["Probe " + i].FormatStringExpression = "\"0\""; });
            var after = await f.Sessions.Require().ReadAsync(m => cases.Select((c, i) => (c.Dax, Fills: FormatFill.MayFill(m.Tables["Sales"].Measures["Probe " + i], "#,0"))).ToArray());
            Assert.True(after.All(x => !x.Fills), "a measure with a format string expression was filled: " + string.Join(" | ", after.Where(x => x.Fills).Select(x => x.Dax)));
        }

        /// <summary>The column half of the same check: an Int64 column that is not a key, an ID, a date key, a code or a
        /// flag. The table's key, a column in a relationship, a sort-by column, a column with a data category, and a
        /// name that marks one of those are all left alone, and so is every column that is not Int64.</summary>
        private static readonly (string Column, string Type, bool MayFill)[] FillColumnCases =
        {
            ("Quantity", "Int64", true),
            ("Units Sold", "Int64", true),
            ("Amount", "Decimal", false),
            ("Order Date", "DateTime", false),
            ("ProductID", "Int64", false),
            ("EMP_ID", "Int64", false),
            ("EMPID", "Int64", false),
            ("DateKey", "Int64", false),
            ("Year", "Int64", false),
            ("Region Code", "Int64", false),
            ("IsActive", "Int64", false),
            ("Return Flag", "Int64", false),
            ("Month Number", "Int64", false),
        };

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Format_fill_may_fill_only_an_int64_column_that_is_a_quantity()
        {
            var cases = FillColumnCases;
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "fill-columns", async b =>
            {
                var e = b.Engine;
                await e.CreateModelAsync("Fill", 1604);
                await e.CreateTableAsync("Sales", "human");
                foreach (var (column, type, _) in cases)
                {
                    await e.CreateColumnAsync("table:Sales", column, type, column.Replace(" ", ""), "human");
                    // Summed on purpose, so the name and the structure are what decide (join: a column fill needs proof it is summed, decision 2).
                    if (type == "Int64") await e.SetObjectPropertyAsync("column:Sales/" + column, "SummarizeBy", "Sum", "human");
                }
            }, "FillColumns.bim");
            var got = await f.Sessions.Require().ReadAsync(m => cases.Select(c => (c.Column, c.MayFill, Fills: FormatFill.MayFill(m.Tables["Sales"].Columns[c.Column], "#,0"))).ToArray());
            foreach (var g in got) Assert.True(g.MayFill == g.Fills, $"{g.Column}: expected MayFill {g.MayFill}, got {g.Fills}");
        }

        /// <summary>The same quantity column stops being fillable when it is what another column sorts by, or a
        /// relationship end: either way it is now a key, not a quantity.</summary>
        [Fact]
        public async Task Format_fill_never_fills_a_quantity_column_used_as_a_sort_key_or_in_a_relationship()
        {
            const string column = "Quantity";
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Fill", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", column, "Int64", column, "human");
            await e.SetObjectPropertyAsync("column:Sales/" + column, "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            Assert.True(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Columns[column], "#,0")));
            await e.CreateTableAsync("Other", "human");
            await e.CreateColumnAsync("table:Other", "Pick", "Int64", "Pick", "human");
            await e.CreateColumnAsync("table:Sales", "Label", "String", "Label", "human");
            await Mutate(f, m => m.Tables["Sales"].Columns["Label"].SortByColumn = m.Tables["Sales"].Columns[column]);
            Assert.False(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Columns[column], "#,0")));
            await Mutate(f, m => m.Tables["Sales"].Columns["Label"].SortByColumn = null);
            await e.CreateRelationshipAsync("column:Sales/" + column, "column:Other/Pick", null, true, "human");
            Assert.False(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Columns[column], "#,0")));
        }

        /// <summary>THE FORMAT FILL RULE on the check's raw-CSV shape, every write path, both doors: after one press, Fix
        /// all, apply_safe_fixes and the change plan, the allowed kinds (counts, a SUM of a whole-number column) are
        /// filled with #,0 and every other blank measure reads exactly as before (still blank). The advice on the ratio
        /// names the suggestion in words, and the deploy gate does not block on any of it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("pro")]
        public Task The_raw_csv_model_fills_only_the_counts_on_every_door(string tier) => The_raw_csv_model_fills_only_the_counts_on_every_doorBodyAsync(tier);

        [Theory]
        [Trait(TestCost.Key, TestCost.Sweep)]
        [InlineData("free")]
        public Task The_raw_csv_model_fills_only_the_counts_on_every_door_on_free(string tier) => The_raw_csv_model_fills_only_the_counts_on_every_doorBodyAsync(tier);

        private async Task The_raw_csv_model_fills_only_the_counts_on_every_doorBodyAsync(string tier)
        {
            using var f = new DecisionFixture(tier == "pro" ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Timesheets", 1604);
            await e.CreateTableAsync("Timesheets", "human");
            foreach (var (name, type) in new[] { ("EMP_ID", "Int64"), ("HOURS_BILLED", "Decimal"), ("HOURS_TOTAL", "Decimal"), ("AMT", "Decimal"),
                                                 ("RATE", "Double"), ("QTY", "Int64"), ("WORK_DATE", "DateTime") })
                await e.CreateColumnAsync("table:Timesheets", name, type, name, "human");
            var measures = new Dictionary<string, (string dax, bool fill)>
            {
                ["HRS_BILL"] = ("SUM ( Timesheets[HOURS_BILLED] )", false), ["HRS_TOT"] = ("SUM ( Timesheets[HOURS_TOTAL] )", false),
                ["UTIL_PCT"] = ("DIVIDE ( [HRS_BILL], [HRS_TOT] )", false), ["UTIL_RATIO"] = ("[HRS_BILL] / [HRS_TOT]", false),
                ["REVENUE"] = ("SUM ( Timesheets[AMT] )", false), ["AVG_RATE"] = ("AVERAGE ( Timesheets[RATE] )", false),
                ["MAX_RATE"] = ("MAX ( Timesheets[RATE] )", false), ["LAST_WORK"] = ("MAX ( Timesheets[WORK_DATE] )", false),
                ["MARGIN_EST"] = ("[REVENUE] * 0.3", false), ["TARGET"] = ("0.73", false),
                ["ROWS"] = ("COUNTROWS ( Timesheets )", true), ["EMPLOYEES"] = ("DISTINCTCOUNT ( Timesheets[EMP_ID] )", true),
                ["QTY_SUM"] = ("SUM ( Timesheets[QTY] )", true),
            };
            foreach (var kv in measures) await e.CreateMeasureAsync("table:Timesheets", kv.Key, kv.Value.dax, "human");
            var rpc = new EngineRpcTarget(e);

            var card = await e.BpaScanAsync();
            Assert.Equal(measures.Where(kv => kv.Value.fill).Select(kv => "measure:Timesheets/" + kv.Key).OrderBy(x => x, StringComparer.Ordinal),
                card.Violations.Where(v => v.RuleId == "INTEGER_FORMATTING" && v.CanAutoFix).Select(v => v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal));
            var advice = await McpTools.GetFixPrompt(e, "bpa", "INTEGER_FORMATTING", "measure:Timesheets/UTIL_PCT");
            Assert.Equal("(advice)", advice.Tool);
            Assert.Contains("It has no format yet, and the rule suggests \"#,0\"", advice.Prompt, StringComparison.Ordinal);
            Assert.Contains("DIVIDE ( [HRS_BILL], [HRS_TOT] )", advice.Prompt, StringComparison.Ordinal);   // the formula, to choose by
            Assert.Equal(JsonSerializer.Serialize(advice), JsonSerializer.Serialize(await rpc.bpaGetFixPrompt("INTEGER_FORMATTING", "measure:Timesheets/UTIL_PCT")));

            foreach (var name in measures.Keys)
            {
                try { await McpTools.ApplyFix(e, "bpa", "INTEGER_FORMATTING", "measure:Timesheets/" + name); } catch (InvalidOperationException) { }
                try { await rpc.bpaFix("INTEGER_FORMATTING", "measure:Timesheets/" + name); } catch (InvalidOperationException) { }
            }
            await rpc.bpaFixAll();
            await McpTools.ApplySafeFixes(e, "bpa");
            await McpTools.ApplySafeFixes(e, "air");
            foreach (var plan in new[] { await McpTools.ProposePlan(e, null, false, 0), await rpc.proposePlan(null, false, 0) })
            {
                var ids = plan.Items.Where(i => i.Kind == "bpa_fix").Select(i => i.Id).ToArray();
                if (ids.Length > 0) await McpTools.ApplyPlan(e, ids);
            }

            var now = await f.Sessions.Require().ReadAsync(m => m.Tables["Timesheets"].Measures.ToDictionary(x => x.Name, x => x.FormatString ?? ""));
            foreach (var kv in measures)
                Assert.True(now[kv.Key] == (kv.Value.fill ? "#,0" : ""), $"{kv.Key} = {kv.Value.dax} reads \"{now[kv.Key]}\" after every path");
            Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["Timesheets"].Columns["WORK_DATE"].FormatString));

            var gate = await e.DeployGateAsync(null, "human");
            Assert.DoesNotContain(gate.Blockers ?? Array.Empty<string>(), b => b != null && b.Contains("Whole numbers", StringComparison.Ordinal));
        }
    }
}
