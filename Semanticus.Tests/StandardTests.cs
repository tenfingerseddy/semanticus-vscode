using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>"Your standard" (1.7): the file, the presets, the order a standard is found in, and the four tools. The promises pinned
    /// here: a standard is found model file first, then your default, then Balanced; a value that cannot be read changes nothing and is
    /// named; a preview writes nothing and moves no revision; apply makes the sure changes as ONE undo step and sends renames and words to
    /// the Plan; grades stay absolute. The happy path runs on a real model with a money measure, a count and a percent.</summary>
    [Collection("restore-root")]   // moves SEMANTICUS_HOME
    public sealed class StandardTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "std" + Guid.NewGuid().ToString("N").Substring(0, 6));
        private readonly string _home;
        private readonly string _originalHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);

        public StandardTests()
        {
            _home = Path.Combine(_root, "home");
            Directory.CreateDirectory(_home);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _home);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _originalHome);
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        // ---- the file --------------------------------------------------------------------------------

        private const string ContosoYaml = @"# Contoso standard.
standard: Contoso
starts-from: balanced
checks:
  level: balanced
  off: [REL-SNOWFLAKE, SNOWFLAKE_SCHEMA_ARCHITECTURE]
  must: [DESC-MEASURE, HIDE_FOREIGN_KEYS]
  extra-rules: contoso-rules.json
names: title-case
formats: { money: ""$#,0.00"", percent: ""0.0%"", count: ""#,0"" }
folders: by-subject, over 10
descriptions: measures, tables
synonyms: 2, measures and tables
keys-and-totals: hide keys, no-sum ids
ai-instructions: template.md
";

        [Fact]
        public void The_design_example_file_reads_every_part()
        {
            var s = StandardYaml.Parse(ContosoYaml, out var problems);
            Assert.Empty(problems);
            Assert.Empty(StandardYaml.Check(s));
            Assert.Equal("Contoso", s.Standard);
            Assert.Equal(new[] { "REL-SNOWFLAKE", "SNOWFLAKE_SCHEMA_ARCHITECTURE" }, s.Checks.Off);
            Assert.Equal(new[] { "DESC-MEASURE", "HIDE_FOREIGN_KEYS" }, s.Checks.Must);
            Assert.Equal("contoso-rules.json", s.Checks.ExtraRules);
            Assert.Equal("$#,0.00", s.Formats["money"]);
            Assert.Equal("title-case", s.Names);
            Assert.Equal("template.md", s.AiInstructions);
        }

        [Fact]
        public void Writing_then_reading_keeps_every_part_including_formats_with_symbols()
        {
            var s = StandardYaml.Parse(ContosoYaml, out _);
            var back = StandardYaml.Parse(StandardYaml.Write(s), out var problems);
            Assert.Empty(problems);
            Assert.Equal(StandardYaml.Write(s), StandardYaml.Write(back));
            Assert.Equal("$#,0.00", back.Formats["money"]);
            Assert.Equal(new[] { "REL-SNOWFLAKE", "SNOWFLAKE_SCHEMA_ARCHITECTURE" }, back.Checks.Off);
        }

        [Theory]
        [InlineData("names: shout", "names is leave")]
        [InlineData("folders: sometimes", "folders is none")]
        [InlineData("synonyms: lots", "synonyms is none")]
        [InlineData("descriptions: everything", "descriptions is none")]
        [InlineData("keys-and-totals: maybe", "keys-and-totals is leave")]
        [InlineData("starts-from: harsh", "starts-from is light")]
        [InlineData("formats: { lava: \"0\" }", "no kind called 'lava'")]
        [InlineData("formats: { money: \"dollars\" }", "no digit place")]
        public void A_value_that_cannot_be_read_is_named_in_plain_words(string line, string expected)
        {
            var s = StandardYaml.Parse(line + "\n", out _);
            Assert.Contains(StandardYaml.Check(s), p => p.Contains(expected));
        }

        [Fact]
        public void An_unknown_part_is_named_and_the_rest_still_reads()
        {
            var s = StandardYaml.Parse("names: title-case\nsparkle: yes\n", out var problems);
            Assert.Equal("title-case", s.Names);
            Assert.Contains(problems, p => p.Contains("'sparkle' is not a part of a standard"));
        }

        [Fact]
        public void The_three_presets_differ_where_the_design_says_they_do()
        {
            var light = StandardPresets.Get("light"); var bal = StandardPresets.Get("balanced"); var strict = StandardPresets.Get("strict");
            Assert.Equal("light", light.Checks.Level); Assert.Equal("balanced", bal.Checks.Level); Assert.Equal("strict", strict.Checks.Level);
            Assert.Equal("leave", bal.Names); Assert.Equal("title-case", strict.Names);
            Assert.Equal("by-subject, over 10", bal.Folders); Assert.Equal("none", light.Folders);
            Assert.Equal("measures, tables", bal.Descriptions); Assert.Equal("all-visible", strict.Descriptions);
            foreach (var p in new[] { light, bal, strict }) Assert.Empty(StandardYaml.Check(p));
        }

        // ---- the order a standard is found in ---------------------------------------------------------

        [Fact]
        public void With_no_file_anywhere_the_standard_is_Balanced_built_in()
        {
            var r = StandardStore.Resolve(null);
            Assert.Equal("built-in", r.Source);
            Assert.Equal("Balanced", r.Name);
            Assert.Null(r.Path);
        }

        [Fact]
        public void Your_default_is_used_when_the_model_has_no_file_and_the_model_file_wins_over_it()
        {
            File.WriteAllText(Path.Combine(_home, "standard.yaml"), "standard: Mine\nnames: title-case\n");
            var model = Path.Combine(_root, "m1");
            Directory.CreateDirectory(Path.Combine(model, ".semanticus"));
            Assert.Equal("your-default", StandardStore.Resolve(model).Source);
            Assert.Equal("Mine", StandardStore.Resolve(model).Name);

            File.WriteAllText(Path.Combine(model, ".semanticus", "standard.yaml"), "standard: Contoso\nstarts-from: strict\n");
            var r = StandardStore.Resolve(model);
            Assert.Equal("model-file", r.Source);
            Assert.Equal("Contoso", r.Name);
            // The model's file is the whole standard: it does not borrow names from your default; strict gives its own.
            Assert.Equal("title-case", r.Effective.Names);
            Assert.Equal("strict", r.Effective.Checks.Level);
        }

        [Fact]
        public void A_use_pointer_reads_the_shared_file_and_the_lines_beside_it_win_for_this_model()
        {
            var shared = Path.Combine(_root, "clients", "contoso.standard.yaml");
            Directory.CreateDirectory(Path.GetDirectoryName(shared));
            File.WriteAllText(shared, "standard: Contoso\nstarts-from: strict\nformats: { money: \"$#,0.00\" }\nnames: title-case\n");
            var model = Path.Combine(_root, "m2");
            Directory.CreateDirectory(Path.Combine(model, ".semanticus"));
            File.WriteAllText(Path.Combine(model, ".semanticus", "standard.yaml"), "use: ../../clients/contoso.standard.yaml\nnames: leave\n");

            var r = StandardStore.Resolve(model);
            Assert.Empty(r.Notes);
            Assert.Equal("Contoso", r.Name);
            Assert.Equal("leave", r.Effective.Names);                  // the line beside use: wins
            Assert.Equal("$#,0.00", r.Effective.Formats["money"]);     // the rest comes from the shared file
            Assert.Equal("strict", r.Effective.Checks.Level);          // and its starts-from picks the preset underneath
        }

        [Fact]
        public void A_shared_file_that_cannot_be_read_is_a_note_and_the_standard_still_works()
        {
            var model = Path.Combine(_root, "m3");
            Directory.CreateDirectory(Path.Combine(model, ".semanticus"));
            File.WriteAllText(Path.Combine(model, ".semanticus", "standard.yaml"), "use: ../nowhere/x.yaml\nstandard: Local\nnames: title-case\n");
            var r = StandardStore.Resolve(model);
            Assert.Contains(r.Notes, n => n.Contains("is not on this computer"));
            Assert.Equal("Local", r.Name);
            Assert.Equal("title-case", r.Effective.Names);
        }

        [Fact]
        public void A_shared_https_address_is_read_through_the_fetcher_and_plain_http_is_a_note()
        {
            var model = Path.Combine(_root, "m4");
            Directory.CreateDirectory(Path.Combine(model, ".semanticus"));
            File.WriteAllText(Path.Combine(model, ".semanticus", "standard.yaml"), "use: https://example.invalid/team.yaml\n");
            var r = StandardStore.Resolve(model, url => "standard: Team\nnames: title-case-no-prefix\n");
            Assert.Equal("Team", r.Name);
            Assert.Equal("title-case-no-prefix", r.Effective.Names);

            File.WriteAllText(Path.Combine(model, ".semanticus", "standard.yaml"), "use: http://example.invalid/team.yaml\n");
            Assert.Contains(StandardStore.Resolve(model, url => throw new InvalidOperationException("must not fetch")).Notes, n => n.Contains("https"));
        }

        [Fact]
        public void A_bad_value_in_the_file_keeps_the_preset_for_that_part_and_says_so()
        {
            var model = Path.Combine(_root, "m5");
            Directory.CreateDirectory(Path.Combine(model, ".semanticus"));
            File.WriteAllText(Path.Combine(model, ".semanticus", "standard.yaml"), "names: shout\nfolders: by-subject, always\n");
            var r = StandardStore.Resolve(model);
            Assert.Contains(r.Notes, n => n.Contains("names is leave"));
            Assert.Equal("leave", r.Effective.Names);
            Assert.Equal("by-subject, always", r.Effective.Folders);
        }

        // ---- what the checks part lets through ---------------------------------------------------------

        [Fact]
        public void Checks_off_must_and_level_decide_which_findings_reach_the_Plan()
        {
            var c = new StandardChecks { Level = "light", Off = new[] { "SYN-FIELD" }, Must = new[] { "DESC-MEASURE", "SYN-FIELD" } };
            Assert.False(StandardChecksFilter.Keeps(c, "SYN-FIELD", "High"));         // off wins
            Assert.True(StandardChecksFilter.Keeps(c, "DESC-MEASURE", "Info"));       // must beats the light level
            Assert.False(StandardChecksFilter.Keeps(c, "NAME-GENERIC", "Medium"));    // light drops what does not change answers
            Assert.True(StandardChecksFilter.Keeps(c, "VIS-FK", "High"));
            Assert.True(StandardChecksFilter.Keeps(new StandardChecks { Level = "balanced" }, "NAME-GENERIC", "Info"));
            Assert.True(StandardChecksFilter.Keeps(null, "ANY", "Info"));
        }

        [Theory]
        [InlineData("dim_customer_name", false, "Dim Customer Name")]
        [InlineData("DimCustomer", true, "Customer")]
        [InlineData("sales amount", false, "Sales Amount")]
        [InlineData("total sales of year", false, "Total Sales of Year")]
        [InlineData("YoY %", false, "YoY %")]
        [InlineData("SalesAmountUSD", false, "Sales Amount USD")]
        [InlineData("Customer", true, "Customer")]
        public void Title_case_reads_like_a_person_wrote_it(string from, bool noPrefix, string expected) =>
            Assert.Equal(expected, StandardNames.TitleCase(from, noPrefix, isTable: true));

        private static async Task<string> PropAsync(LocalEngine engine, string objRef, string name) =>
            (await engine.GetObjectPropertiesAsync(objRef)).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

        // ---- the engine: a real model ----------------------------------------------------------------

        private async Task<(LocalEngine engine, string dir)> SalesModelAsync(bool save = true)
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.CreateModelAsync("StdTest", 1604);
            var sales = await engine.CreateTableAsync("sales_fact", "agent");
            var amount = await engine.CreateColumnAsync(sales, "net_amount", "Decimal", "net_amount", "agent");
            await engine.SetObjectPropertyAsync(amount, "FormatString", "$#,0.00", "agent");
            var qty = await engine.CreateColumnAsync(sales, "quantity", "Int64", "quantity", "agent");
            var fk = await engine.CreateColumnAsync(sales, "customer_key", "Int64", "customer_key", "agent");
            var customer = await engine.CreateTableAsync("DimCustomer", "agent");
            var ck = await engine.CreateColumnAsync(customer, "customer_key", "Int64", "customer_key", "agent");
            await engine.SetObjectPropertyAsync(ck, "IsKey", "true", "agent");
            await engine.CreateColumnAsync(customer, "customer_name", "String", "customer_name", "agent");
            await engine.CreateRelationshipAsync(fk, ck, null, true, "agent");
            await engine.CreateMeasureAsync(sales, "total amount", "SUM ( sales_fact[net_amount] )", "agent");
            await engine.CreateMeasureAsync(sales, "order count", "COUNTROWS ( sales_fact )", "agent");
            var pct = await engine.CreateMeasureAsync(sales, "margin pct", "DIVIDE ( [total amount] * 0.3, [total amount] )", "agent");
            await engine.SetMeasureFormatAsync(pct, "0%", "agent");
            string dir = null;
            if (save)
            {
                dir = Path.Combine(_root, "model");
                await engine.SaveAsync(dir, "TMDL", false, "human");
            }
            return (engine, dir);
        }

        [Fact]
        public async Task Get_standard_on_a_model_with_no_file_is_Balanced_with_the_three_presets()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                var g = await engine.GetStandardAsync();
                Assert.Equal("Balanced", g.Standard.Name);
                Assert.Equal("built-in", g.Standard.Source);
                Assert.Equal(new[] { "Light", "Balanced", "Strict" }, g.Presets.Select(p => p.Standard).ToArray());
                Assert.Null(g.Standard.CheckedAgainst);
            }
        }

        [Fact]
        public async Task Set_standard_writes_the_model_file_and_get_standard_reads_it_back()
        {
            var (engine, dir) = await SalesModelAsync();
            using (engine)
            {
                var r = await engine.SetStandardAsync("{\"preset\":\"strict\",\"standard\":\"Contoso\",\"formats\":{\"money\":\"$#,0.00\"},\"checks\":{\"off\":[\"REL-SNOWFLAKE\"]}}", null, "agent");
                Assert.Equal(Path.Combine(dir, ".semanticus", "standard.yaml"), r.Path);
                Assert.True(File.Exists(r.Path));
                Assert.Contains("the check", string.Join(" ", r.Changed));
                var g = await engine.GetStandardAsync();
                Assert.Equal("model-file", g.Standard.Source);
                Assert.Equal("Contoso", g.Standard.Name);
                Assert.Equal("$#,0.00", g.Standard.Effective.Formats["money"]);
                Assert.Equal("strict", g.Standard.Effective.Checks.Level);
                Assert.Equal(new[] { "REL-SNOWFLAKE" }, g.Standard.Effective.Checks.Off);
                Assert.Contains("REL-SNOWFLAKE", g.Standard.FileText);

                // A second patch changes one part and keeps the rest; null goes back to the preset.
                await engine.SetStandardAsync("{\"names\":\"title-case-no-prefix\",\"formats\":{\"money\":null}}", null, "agent");
                var g2 = await engine.GetStandardAsync();
                Assert.Equal("title-case-no-prefix", g2.Standard.Effective.Names);
                Assert.False(g2.Standard.Effective.Formats.ContainsKey("money"));
                Assert.Equal("Contoso", g2.Standard.Name);
            }
        }

        [Fact]
        public async Task A_bad_patch_changes_nothing_and_names_the_value()
        {
            var (engine, dir) = await SalesModelAsync();
            using (engine)
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetStandardAsync("{\"names\":\"shout\",\"sparkle\":1}", null, "agent"));
                Assert.Contains("Nothing was changed", ex.Message);
                Assert.Contains("names is leave", ex.Message);
                Assert.Contains("'sparkle'", ex.Message);
                Assert.False(File.Exists(Path.Combine(dir, ".semanticus", "standard.yaml")));
            }
        }

        [Fact]
        public async Task Set_standard_to_your_default_writes_the_user_file_that_every_unfiled_model_uses()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                var r = await engine.SetStandardAsync("{\"standard\":\"House\",\"names\":\"title-case\"}", "your-default", "agent");
                Assert.Equal(Path.Combine(_home, "standard.yaml"), r.Path);
                var g = await engine.GetStandardAsync();
                Assert.Equal("your-default", g.Standard.Source);
                Assert.Equal("House", g.Standard.Name);
            }
        }

        [Fact]
        public async Task Preview_writes_nothing_moves_no_revision_and_keeps_the_grade_absolute()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                await engine.SetStandardAsync("{\"preset\":\"strict\",\"formats\":{\"money\":\"$#,0.00\"}}", null, "agent");
                var revision = (await engine.SessionInfoAsync()).Revision;
                var gradeNow = (await engine.AiReadinessScanAsync()).Grade;

                var p = await engine.PreviewStandardAsync();

                Assert.Equal(revision, (await engine.SessionInfoAsync()).Revision);
                Assert.Equal(gradeNow, p.GradeNow);
                Assert.Equal(gradeNow, (await engine.AiReadinessScanAsync()).Grade);
                Assert.Equal(StandardParts.All, p.Parts.Select(x => x.Part).ToArray());
                // Nothing in the model moved: the money measure still has no format, the FK is still visible.
                Assert.True(string.IsNullOrEmpty(await PropAsync(engine, "measure:sales_fact/total amount", "FormatString")));
                Assert.True(p.ChangesNow > 0);
                Assert.True(p.Tasks > 0);
            }
        }

        [Fact]
        public async Task Preview_with_a_patch_shows_the_standard_with_that_patch_on_top_and_writes_no_file()
        {
            var (engine, dir) = await SalesModelAsync();
            using (engine)
            {
                var file = Path.Combine(dir, ".semanticus", "standard.yaml");
                var plain = await engine.PreviewStandardAsync();
                var strict = await engine.PreviewStandardAsync("{\"preset\":\"strict\"}");

                Assert.Equal("Strict", strict.Standard.Name);
                Assert.True(strict.ChangesNow + strict.Tasks > plain.ChangesNow + plain.Tasks, "Strict asks for more than Balanced on this model");
                Assert.False(File.Exists(file), "a preview with a patch saves nothing");
                Assert.Equal("Balanced", (await engine.GetStandardAsync()).Standard.Name);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PreviewStandardAsync("{\"names\":\"shout\"}"));
                Assert.Contains("Nothing was previewed", ex.Message);
            }
        }

        [Fact]
        public async Task Apply_holds_the_model_to_the_standard_as_one_undo_step_and_sends_renames_and_words_to_the_Plan()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                await engine.SetStandardAsync(
                    "{\"preset\":\"strict\",\"standard\":\"Contoso\",\"names\":\"title-case-no-prefix\",\"formats\":{\"money\":\"$#,0.00\",\"percent\":\"0.0%\",\"count\":\"#,0\"},\"descriptions\":\"measures\",\"folders\":\"by-subject, always\"}",
                    null, "agent");
                var preview = await engine.PreviewStandardAsync();

                var r = await engine.ApplyStandardAsync("agent");

                // The sure changes were made now.
                string Dump(StandardPartResult[] ps) => string.Join("; ", ps.Select(x => x.Part + " now=" + x.ChangesNow + " tasks=" + x.Tasks));
                Assert.True(preview.ChangesNow == r.ChangesNow, "preview: " + Dump(preview.Parts) + " | apply: " + Dump(r.Parts));
                Assert.Equal(preview.Tasks, r.Tasks);
                string Format(string measure) => PropAsync(engine, "measure:sales_fact/" + measure, "FormatString").GetAwaiter().GetResult();
                Assert.Equal("$#,0.00", Format("total amount"));
                Assert.Equal("#,0", Format("order count"));
                Assert.Equal("0.0%", Format("margin pct"));
                Assert.Equal("True", await PropAsync(engine, "column:sales_fact/customer_key", "IsHidden"), ignoreCase: true);
                Assert.Contains(r.Parts, p => p.Part == StandardParts.Folders && p.ChangesNow > 0);

                // Renames and descriptions were NOT made now: they are Plan tasks.
                Assert.NotNull(await engine.GetObjectAsync("table:DimCustomer"));
                Assert.NotNull(await engine.GetObjectAsync("measure:sales_fact/total amount"));
                Assert.Contains(r.Parts, p => p.Part == StandardParts.Names && p.Tasks >= 5 && p.Examples.Length == 3 && p.Examples.Any(e => e.Contains("sales_fact to Sales Fact")));
                var plan = await engine.GetModelPlanAsync();
                Assert.Contains(plan.Items, i => (i.Title ?? "").StartsWith("Rename "));
                Assert.Contains(plan.Items, i => (i.Title ?? "").StartsWith("Write ") && (i.Title ?? "").Contains("description"));
                Assert.Equal(preview.Tasks, r.PlanRows.Length);   // names, descriptions, and the synonyms the strict preset asks for

                // The model says which standard it was checked against.
                Assert.Equal("Checked against Contoso", (await engine.GetStandardAsync()).Standard.CheckedAgainst);

                // One undo step puts every change back.
                await engine.UndoAsync("human");
                Assert.True(string.IsNullOrEmpty(Format("total amount")));
                Assert.True(string.IsNullOrEmpty(Format("order count")));
                Assert.Equal("False", await PropAsync(engine, "column:sales_fact/customer_key", "IsHidden"), ignoreCase: true);
                Assert.Null((await engine.GetStandardAsync()).Standard.CheckedAgainst);
            }
        }

        [Fact]
        public async Task Undo_of_an_Apply_takes_its_Plan_rows_away_and_Redo_brings_them_back()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                await engine.SetStandardAsync("{\"names\":\"title-case\",\"formats\":{\"money\":\"$#,0.00\"}}", null, "agent");
                var r = await engine.ApplyStandardAsync("agent");
                Assert.True(r.PlanRows.Length > 0);
                async Task<string[]> Mine() { var ids = (await engine.GetModelPlanAsync()).Items.Select(i => i.Id).ToHashSet(); return r.PlanRows.Where(ids.Contains).ToArray(); }
                Assert.Equal(r.PlanRows.Length, (await Mine()).Length);

                await engine.UndoAsync("human");
                Assert.Empty(await Mine());

                await engine.RedoAsync("human");
                Assert.Equal(r.PlanRows.Length, (await Mine()).Length);

                // Undo, then Apply again: fresh rows, not the old ones and a copy.
                await engine.UndoAsync("human");
                var again = await engine.ApplyStandardAsync("agent");
                Assert.Equal(r.PlanRows.Length, again.PlanRows.Length);
                var titles = (await engine.GetModelPlanAsync()).Items.Where(i => again.PlanRows.Contains(i.Id)).Select(i => i.Title).ToArray();
                Assert.Equal(titles.Length, titles.Distinct().Count());
            }
        }

        [Fact]
        public async Task The_Names_count_is_the_same_before_Apply_on_the_Plan_row_and_after_Apply()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                // Keys are hidden in the same pass, and a hidden column is not renamed: every count must leave them out.
                await engine.SetStandardAsync("{\"names\":\"title-case\",\"keys-and-totals\":\"hide keys, no-sum ids\"}", null, "agent");
                var before = (await engine.PreviewStandardAsync()).Parts.Single(p => p.Part == StandardParts.Names).Tasks;
                var r = await engine.ApplyStandardAsync("agent");
                Assert.Contains(r.Parts, p => p.Part == StandardParts.KeysAndTotals && p.ChangesNow > 0);
                var applied = r.Parts.Single(p => p.Part == StandardParts.Names).Tasks;
                var after = (await engine.PreviewStandardAsync()).Parts.Single(p => p.Part == StandardParts.Names).Tasks;
                var row = (await engine.GetModelPlanAsync()).Items.Single(i => r.PlanRows.Contains(i.Id) && (i.Title ?? "").StartsWith("Rename "));
                Assert.Equal("Rename " + applied + " objects to the standard (title case)", row.Title);
                Assert.Equal(applied, after);
                Assert.Equal(applied, before);
            }
        }

        [Fact]
        public async Task Applying_twice_changes_nothing_the_second_time_and_adds_no_second_Plan_row()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                await engine.SetStandardAsync("{\"names\":\"title-case\",\"formats\":{\"money\":\"$#,0.00\"}}", null, "agent");
                var first = await engine.ApplyStandardAsync("agent");
                var second = await engine.ApplyStandardAsync("agent");
                Assert.True(first.ChangesNow > 0);
                Assert.Equal(0, second.ChangesNow);
                var plan = await engine.GetModelPlanAsync();
                Assert.Single(plan.Items, i => (i.Title ?? "").StartsWith("Rename "));
            }
        }

        [Fact]
        public async Task A_measure_whose_kind_the_model_does_not_prove_is_left_alone_and_counted()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                var sales = "table:sales_fact";
                await engine.CreateMeasureAsync(sales, "mystery", "[total amount] / 7", "agent");
                await engine.SetStandardAsync("{\"formats\":{\"money\":\"$#,0.00\",\"count\":\"#,0\"}}", null, "agent");
                var r = await engine.ApplyStandardAsync("agent");
                Assert.True(string.IsNullOrEmpty(await PropAsync(engine, "measure:sales_fact/mystery", "FormatString")));
                Assert.Contains("left for you", r.Parts.Single(p => p.Part == StandardParts.Formats).Note);
            }
        }

        [Fact]
        public async Task A_check_the_standard_turns_off_never_reaches_the_Plan_and_the_grade_does_not_move()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                var gradeBefore = (await engine.AiReadinessScanAsync());
                var planBefore = await engine.GetModelPlanAsync();
                Assert.Contains(planBefore.Items, i => i.Rules != null && i.Rules.Contains("VIS-FK"));

                await engine.SetStandardAsync("{\"checks\":{\"off\":[\"VIS-FK\"]}}", null, "agent");

                var planAfter = await engine.GetModelPlanAsync();
                Assert.DoesNotContain(planAfter.Items, i => i.Rules != null && i.Rules.Contains("VIS-FK"));
                var scanAfter = await engine.AiReadinessScanAsync();
                Assert.Equal(gradeBefore.Grade, scanAfter.Grade);
                Assert.Contains(scanAfter.Findings, f => f.RuleId == "VIS-FK");   // still found, still scored: only the Plan is quieter
            }
        }

        [Fact]
        public async Task A_best_practice_rule_the_standard_turns_off_never_reaches_the_Plan_either()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                var before = await engine.GetModelPlanAsync();
                Assert.Contains(before.Items, i => i.Id == "bpafix/model" && i.Rules.Contains("HIDE_FOREIGN_KEYS"));

                await engine.SetStandardAsync("{\"checks\":{\"off\":[\"HIDE_FOREIGN_KEYS\"]}}", null, "agent");

                var after = await engine.GetModelPlanAsync();
                Assert.DoesNotContain(after.Items, i => i.Rules != null && i.Rules.Contains("HIDE_FOREIGN_KEYS"));
                Assert.Contains((await engine.BpaScanAsync()).Violations, f => f.RuleId == "HIDE_FOREIGN_KEYS");   // still found and still scored
            }
        }

        [Fact]
        public async Task The_page_door_and_the_assistant_door_reach_the_same_standard_on_the_same_timeline()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                var rpc = new EngineRpcTarget(engine);
                // The assistant tunes it in words; the page reads the same file back and applies it; one undo takes the apply back.
                var set = await McpTools.SetStandard(engine, "{\"formats\":{\"money\":\"$#,0.00\"}}");
                Assert.Contains("the money format", set.Changed);
                Assert.Equal("$#,0.00", (await rpc.getStandard()).Standard.Effective.Formats["money"]);

                var viaPage = await rpc.previewStandard();
                var viaAssistant = await McpTools.PreviewStandard(engine);
                Assert.Equal(viaPage.ChangesNow, viaAssistant.ChangesNow);
                Assert.Equal(viaPage.Tasks, viaAssistant.Tasks);

                var applied = await rpc.applyStandard();
                Assert.True(applied.ChangesNow > 0);
                Assert.Equal("$#,0.00", await PropAsync(engine, "measure:sales_fact/total amount", "FormatString"));
                var again = await McpTools.ApplyStandard(engine);
                Assert.Equal(0, again.ChangesNow);
                await engine.UndoAsync("human");
                Assert.True(string.IsNullOrEmpty(await PropAsync(engine, "measure:sales_fact/total amount", "FormatString")));
            }
        }

        [Fact]
        public async Task The_standards_extra_rules_file_is_merged_into_the_model_as_part_of_the_apply()
        {
            var (engine, dir) = await SalesModelAsync();
            using (engine)
            {
                File.WriteAllText(Path.Combine(dir, ".semanticus", "contoso-rules.json"),
                    "[{\"ID\":\"CONTOSO_NO_TEMP\",\"Name\":\"No temp measures\",\"Category\":\"Naming Conventions\",\"Description\":\"x\",\"Severity\":1,\"Scope\":\"Measure\",\"Expression\":\"Name.StartsWith(\\\"tmp\\\")\",\"CompatibilityLevel\":1200}]");
                Directory.CreateDirectory(Path.Combine(dir, ".semanticus"));
                await engine.SetStandardAsync("{\"checks\":{\"extra-rules\":\"contoso-rules.json\"}}", null, "agent");
                var r = await engine.ApplyStandardAsync("agent");
                Assert.Contains(r.Applied, a => a.Contains("extra best-practice rules"));
                var scan = await engine.BpaScanAsync();
                Assert.NotNull(scan);
            }
        }

        /// <summary>v-m17 (18 Oct): on Northstar, a standard with percent "0.0%" (Strict, or Change one thing) turned "0.0%;-0.0%;0.0%" into "0.0%". The two show every number the same way
        /// (one decimal, a minus for a negative, 0.0% for zero), so the change was churn on a format the person wrote on purpose. A standard
        /// leaves a format alone when it already shows numbers the way the standard asks.</summary>
        [Fact]
        public async Task Apply_leaves_a_percent_format_alone_when_it_already_shows_numbers_the_standard_way()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                await engine.SetMeasureFormatAsync("measure:sales_fact/margin pct", "0.0%;-0.0%;0.0%", "agent");
                await engine.SetStandardAsync("{\"preset\":\"balanced\",\"formats\":{\"percent\":\"0.0%\"}}", null, "agent");

                var preview = await engine.PreviewStandardAsync();
                await engine.ApplyStandardAsync("agent");

                Assert.Equal("0.0%;-0.0%;0.0%", await PropAsync(engine, "measure:sales_fact/margin pct", "FormatString"));
                // The formats part offers nothing for it: no example line and no counted change (other parts may name the measure, e.g. other names).
                var formats = preview.Parts.Single(p => p.Part == StandardParts.Formats);
                Assert.DoesNotContain(formats.Examples, e => e.Contains("margin pct"));
                Assert.Equal(0, formats.ChangesNow);
            }
        }

        /// <summary>v-m17 judged it: Apply hid four foreign keys and the page then said "Health: 4 new issues", because a hidden column nothing sorts by
        /// is what ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS asks to switch off. The standard raised findings it made itself, on a change that made
        /// the model better. Hiding a key now also turns its MDX flag off, the same write that rule asks for, so the scan after Apply has nothing new.</summary>
        [Fact]
        public async Task Hiding_keys_does_not_create_the_findings_hiding_a_column_causes()
        {
            var (engine, _) = await SalesModelAsync();
            using (engine)
            {
                const string rule = "ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS";
                var before = (await engine.BpaScanAsync()).Violations.Count(v => v.RuleId == rule);
                await engine.SetStandardAsync("{\"preset\":\"balanced\"}", null, "agent");

                await engine.ApplyStandardAsync("agent");

                Assert.Equal("True", await PropAsync(engine, "column:sales_fact/customer_key", "IsHidden"), ignoreCase: true);
                Assert.Equal("False", await PropAsync(engine, "column:sales_fact/customer_key", "IsAvailableInMDX"), ignoreCase: true);
                Assert.Equal(before, (await engine.BpaScanAsync()).Violations.Count(v => v.RuleId == rule));

                // Undo is still one step: the flag comes back with the hide.
                await engine.UndoAsync("human");
                Assert.Equal("True", await PropAsync(engine, "column:sales_fact/customer_key", "IsAvailableInMDX"), ignoreCase: true);
            }
        }

        // ---- a web address is fetched only when the person opens, applies or sets (1.7 threat check, DEC-0017) ----

        [Fact]
        public async Task A_Plan_read_never_fetches_a_web_standard_but_open_and_apply_do_once_each_and_a_failed_fetch_keeps_the_last_good_copy()
        {
            var (engine, dir) = await SalesModelAsync();
            var requests = 0; var down = false;
            StandardStore.ForgetSharedCopies();
            StandardStore.UrlReaderForTest = url => { requests++; if (down) throw new System.Net.Http.HttpRequestException("offline"); return "standard: Team\nnames: title-case-no-prefix\n"; };
            try
            {
                using (engine)
                {
                    File.WriteAllText(StandardStore.ModelFile(dir), "use: https://example.invalid/team.yaml\n");

                    await engine.GetModelPlanAsync();
                    await engine.GetModelPlanAsync();
                    Assert.Equal(0, requests);   // the Plan reads the cache, and there is none yet

                    var opened = await engine.GetStandardAsync();
                    Assert.Equal(1, requests);
                    Assert.Equal("Team", opened.Standard.Name);

                    await engine.GetModelPlanAsync();
                    Assert.Equal(1, requests);   // the Plan used the copy

                    await engine.ApplyStandardAsync("human");
                    Assert.Equal(2, requests);   // apply fetched once

                    down = true;
                    var offline = await engine.GetStandardAsync();
                    Assert.Equal("Team", offline.Standard.Name);   // the last good copy stands
                    Assert.Contains(offline.Standard.Notes, n => n.Contains("could not fetch") && n.Contains("last good copy"));

                    // After a restart the sidecar still holds the copy, so a Plan read works offline.
                    StandardStore.ForgetSharedCopies();
                    var before = requests;
                    Assert.Equal("Team", StandardStore.Resolve(dir).Name);
                    Assert.Equal(before, requests);
                }
            }
            finally { StandardStore.UrlReaderForTest = null; StandardStore.ForgetSharedCopies(); }
        }

        [Fact]
        public void With_no_copy_and_no_fetch_a_web_standard_says_it_has_not_been_fetched_and_the_preset_stands()
        {
            var model = Path.Combine(_root, "m-nofetch");
            Directory.CreateDirectory(Path.Combine(model, ".semanticus"));
            File.WriteAllText(Path.Combine(model, ".semanticus", "standard.yaml"), "use: https://example.invalid/never.yaml\nnames: title-case\n");
            StandardStore.ForgetSharedCopies();
            var r = StandardStore.Resolve(model);
            Assert.Contains(r.Notes, n => n.Contains("has not been fetched yet"));
            Assert.Equal("title-case", r.Effective.Names);
        }
    }
}
