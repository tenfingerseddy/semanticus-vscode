using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Rule clashes found by the M11 Plan statement check (28 Sep, "Defects" item 7). An assistant took a raw model to
    /// grade A using only Semanticus tools, and on the way the rules fought the product and each other: a rule fired on
    /// what the product's own build had made, a rule fired on what another rule had asked for, and one fix rewrote a
    /// valid format the assistant had just set. Each test reproduces one clash on a small model and was watched failing
    /// before its fix, and each carries a control proving the rule still fires on the population it is really about.
    /// </summary>
    public sealed class PlanStatementRuleClashTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public PlanStatementRuleClashTests(ModelTemplates templates) => _templates = templates;

        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        private static async Task<string[]> FlaggedAsync(DecisionFixture f, string ruleId) =>
            (await f.Engine.BpaScanAsync()).Violations.Where(v => v.RuleId == ruleId && !v.Waived)
                .Select(v => v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        // ---- (a) the product's own date table is not a calculated table to migrate ------------------------------

        /// <summary>A spec with a date table builds a CALENDAR calculated table and marks it, which is what the SQL draft
        /// and "Start from scratch" both produce. "Reduce usage of calculated tables" then told the person to move it to
        /// the warehouse, so a model built by the product could never be clean without a waiver.</summary>
        [Fact]
        public async Task The_date_table_the_spec_build_makes_is_not_raised_as_a_calculated_table_to_migrate()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("SpecDate", 1604);
            var spec = new ModelSpec
            {
                Name = "SpecDate",
                CompatibilityLevel = 1604,
                StorageMode = "import",
                Tables = new[] { new SpecTable { Name = "Sales", Role = "fact", Columns = new[] { new SpecColumn { Name = "Amount", DataType = "Decimal" } } } },
                DateTable = new SpecDateTable { Name = "Date", MarkAsDate = true },
            };
            await f.Engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            var report = await f.Engine.BuildModelFromSpecAsync("human");
            Assert.Contains("table:Date", report.Created);
            Assert.True(await f.Sessions.Require().ReadAsync(m => m.Tables["Date"] is CalculatedTable));   // the setup really is a calculated table

            Assert.DoesNotContain("table:Date", await FlaggedAsync(f, "REDUCE_USAGE_OF_CALCULATED_TABLES"));
        }

        /// <summary>The control for (a). Only a table that IS a generated calendar is left alone: CALENDAR or CALENDARAUTO
        /// as the whole expression, or as the table ADDCOLUMNS extends, whatever the case and spacing. A calculated table
        /// whose logic is anything else, including one that merely uses a calendar somewhere inside, is still raised.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("CALENDAR ( DATE ( 2020, 1, 1 ), DATE ( 2026, 12, 31 ) )", false)]
        [InlineData("calendarauto()", false)]
        [InlineData("\r\n  CALENDARAUTO ( 6 )", false)]
        [InlineData("ADDCOLUMNS ( CALENDARAUTO (), \"Year\", YEAR ( [Date] ) )", false)]
        [InlineData("ADDCOLUMNS(\n    CALENDAR ( DATE ( 2020, 1, 1 ), DATE ( 2026, 12, 31 ) ),\n    \"Month\", MONTH ( [Date] ) )", false)]
        [InlineData("SUMMARIZE ( Sales, Sales[Amount] )", true)]
        [InlineData("FILTER ( CROSSJOIN ( VALUES ( Sales[Amount] ), CALENDARAUTO () ), Sales[Amount] > 0 )", true)]
        [InlineData("ADDCOLUMNS ( VALUES ( Sales[Amount] ), \"Double\", Sales[Amount] * 2 )", true)]
        [InlineData("DATATABLE ( \"Calendar\", STRING, { { \"x\" } } )", true)]
        public async Task Only_a_generated_calendar_is_spared_the_calculated_table_rule(string dax, bool raised)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "calc-tables", async b =>
            {
                await b.Engine.CreateModelAsync("CalcTables", 1604);
                await b.Engine.CreateTableAsync("Sales", "human");
                await b.Engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
                // Set through the model rather than create_calculated_table: the write guard's offline DAX check does not
                // resolve [Date] inside ADDCOLUMNS over a calendar, and what is under test here is the rule, not the guard.
                await b.Engine.CreateCalculatedTableAsync("Probe", "CALENDARAUTO ()", "human");
            }, "CalcTables.bim");
            await Mutate(f, m => ((CalculatedTable)m.Tables["Probe"]).Expression = dax);

            Assert.Equal(raised, (await FlaggedAsync(f, "REDUCE_USAGE_OF_CALCULATED_TABLES")).Contains("table:Probe"));
        }

        // ---- (b) a hidden key is not an unnecessary column ------------------------------------------------------

        /// <summary>A two-table model where the date table is marked on a hidden DateTime key and the relationship runs on
        /// an integer DateKey, the shape Desktop and most warehouses produce.</summary>
        private async Task<DecisionFixture> HiddenKeysAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "hidden-keys", BuildHiddenKeysAsync, "HiddenKeys.bim");
            return f;
        }

        private static async Task BuildHiddenKeysAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("HiddenKeys", 1604);
            await e.CreateTableAsync("Invoices", "human");
            await e.CreateColumnAsync("table:Invoices", "Invoice Number", "String", "Invoice Number", "human");
            await e.CreateColumnAsync("table:Invoices", "DateKey", "Int64", "DateKey", "human");
            await e.CreateColumnAsync("table:Invoices", "Line Id", "Int64", "Line Id", "human");
            await e.CreateColumnAsync("table:Invoices", "Load Batch ID", "Int64", "Load Batch ID", "human");
            await e.CreateTableAsync("Calendar", "human");
            await e.CreateColumnAsync("table:Calendar", "Date", "DateTime", "Date", "human");
            await e.CreateColumnAsync("table:Calendar", "DateKey", "Int64", "DateKey", "human");
            await e.CreateRelationshipAsync("column:Invoices/DateKey", "column:Calendar/DateKey", null, true, "human");
            await Mutate(f, m =>
            {
                m.Tables["Calendar"].DataCategory = "Time";
                m.Tables["Calendar"].Columns["Date"].IsKey = true;
                m.Tables["Invoices"].Columns["Invoice Number"].IsKey = true;
                foreach (var c in new[] { m.Tables["Calendar"].Columns["Date"], m.Tables["Invoices"].Columns["Invoice Number"],
                                          m.Tables["Invoices"].Columns["Line Id"], m.Tables["Invoices"].Columns["Load Batch ID"] })
                    c.IsHidden = true;
            });
        }

        /// <summary>BPA contradicted itself: "Model should have a date table" and "Date tables should be marked" both need
        /// a DateTime key column, and "Remove unnecessary columns" told the person to delete that same column once it was
        /// hidden. Deleting it reopened both date findings. A declared key (IsKey, at most one per table) is the row
        /// identity the model uses, which reference counting cannot see, so it is never raised as unnecessary.</summary>
        [Fact]
        public async Task A_hidden_key_column_is_never_raised_as_an_unnecessary_column()
        {
            using var f = await HiddenKeysAsync();
            var flagged = await FlaggedAsync(f, "UNNECESSARY_COLUMNS");
            Assert.DoesNotContain("column:Calendar/Date", flagged);
            Assert.DoesNotContain("column:Invoices/Invoice Number", flagged);
            // The key is what keeps both date rules quiet, so the three rules now agree on one clean state.
            Assert.Empty(await FlaggedAsync(f, "MODEL_SHOULD_HAVE_A_DATE_TABLE"));
        }

        /// <summary>The control for (b). Only the declared key is spared. A hidden row id that is not the key and a hidden
        /// load-batch audit column are still raised: nothing uses them, they cost memory, and removing them is the clean
        /// end state readiness agrees with (it only ever asks to hide such a column from Copilot).</summary>
        [Fact]
        public async Task A_hidden_column_that_is_not_the_key_is_still_raised_as_unnecessary()
        {
            using var f = await HiddenKeysAsync();
            var flagged = await FlaggedAsync(f, "UNNECESSARY_COLUMNS");
            Assert.Contains("column:Invoices/Line Id", flagged);
            Assert.Contains("column:Invoices/Load Batch ID", flagged);
        }

        /// <summary>A key the spec declares on two columns. TOM keeps IsKey on one column per table, so the build used to
        /// leave the other part hidden, unreferenced and not a key, and "Remove unnecessary columns" asked to delete half of
        /// the key (the verifier's ref_codes[CODE_TYPE], a publish blocker on the SQL draft's own model). Every declared
        /// part is now marked, whichever one TOM holds IsKey on, and none is raised. The control: a hidden column beside
        /// the key that nothing uses is still raised in the same table.</summary>
        [Fact]
        public async Task Every_part_of_a_key_the_spec_declares_is_spared_and_a_hidden_column_beside_it_is_still_raised()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("SpecKey", 1604);
            var spec = new ModelSpec
            {
                Name = "SpecKey",
                CompatibilityLevel = 1604,
                StorageMode = "import",
                Tables = new[]
                {
                    new SpecTable
                    {
                        Name = "Order Lines", Role = "isolated", Columns = new[]
                        {
                            new SpecColumn { Name = "Order Number", DataType = "String", IsKey = true, Hidden = true },
                            new SpecColumn { Name = "Line Number", DataType = "Int64", IsKey = true, Hidden = true },
                            new SpecColumn { Name = "Load Batch ID", DataType = "Int64", Hidden = true },
                            new SpecColumn { Name = "Quantity", DataType = "Int64" },
                        },
                    },
                },
            };
            await f.Engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            await f.Engine.BuildModelFromSpecAsync("human");
            var (keys, marked) = await f.Sessions.Require().ReadAsync(m =>
            {
                var cols = m.Tables["Order Lines"].Columns;
                return (cols.Count(c => c.IsKey), cols.Where(c => c.HasAnnotation(SourceKeys.Annotation)).Select(c => c.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            });
            Assert.Equal(1, keys);   // the setup: TOM really holds IsKey on only one of the two
            Assert.Equal(new[] { "Line Number", "Order Number" }, marked);

            var flagged = await FlaggedAsync(f, "UNNECESSARY_COLUMNS");
            Assert.DoesNotContain("column:Order Lines/Order Number", flagged);
            Assert.DoesNotContain("column:Order Lines/Line Number", flagged);
            Assert.Contains("column:Order Lines/Load Batch ID", flagged);
        }

        /// <summary>The readiness half of (b), and the verifier's D2. VIS-TECH-COLUMN asked only to hide a technical
        /// column, and the moment it was hidden and unused BPA asked to remove it, so following one rule opened the other.
        /// The first answer told the reader to remove it "if no report uses it" and said BPA asked the same, which was false
        /// for a relationship's key or a table's key and would have deleted it. The text now names removal exactly when BPA
        /// raises the column once it is hidden, and otherwise says to keep the column and why: each shape here is one
        /// clause of UNNECESSARY_COLUMNS, and the finding and the BPA scan must agree on every one. The 1200 rows are a
        /// model older than object-level security (compatibility level 1400), where TOM has no such setting to read: the
        /// first version of this text read it anyway and the whole readiness scan threw (AirSmoke, AdventureWorks). There BPA
        /// cannot evaluate UNNECESSARY_COLUMNS either (it reports the rule as unknown), so the text names removal without
        /// naming Model quality.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("nothing uses it", true, 1604)]
        [InlineData("relationship", false, 1604)]
        [InlineData("iskey", false, 1604)]
        [InlineData("declared key part", false, 1604)]
        [InlineData("sort by", false, 1604)]
        [InlineData("hierarchy", false, 1604)]
        [InlineData("measure", false, 1604)]
        [InlineData("security filter", false, 1604)]
        [InlineData("nothing uses it", true, 1200)]
        [InlineData("relationship", false, 1200)]
        [InlineData("security filter", false, 1200)]
        public async Task The_technical_column_finding_advises_removal_exactly_when_model_quality_would_raise_it(string shape, bool removable, int compatibilityLevel)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            // A fresh model starts at 1465 or above, so the older level is Microsoft's AdventureWorks sample (1200), with
            // two probe tables whose names it does not use.
            var (sales, customer) = compatibilityLevel < 1400 ? ("Probe Sales", "Probe Customer") : ("Sales", "Customer");
            // One build per compatibility level for the whole class; each row opens its own copy.
            await _templates.OpenCopyAsync(f, "techcol-" + compatibilityLevel, async b =>
            {
                var be = b.Engine;
                if (compatibilityLevel < 1400)
                {
                    var bim = Path.Combine(Path.GetTempPath(), "smx-techcol-" + Guid.NewGuid().ToString("N") + ".bim");
                    File.Copy(TestModels.FindBim(), bim);
                    await be.OpenAsync(bim);
                    await Mutate(b, m =>
                    {
                        var st = m.AddTable(sales);
                        st.AddDataColumn("Customer_SK", "Customer_SK", null, DataType.Int64);
                        st.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                        var ct = m.AddTable(customer);
                        ct.AddDataColumn("Customer_SK", "Customer_SK", null, DataType.Int64);
                        ct.AddDataColumn("Customer Name", "Customer Name", null, DataType.String);
                    });
                }
                else
                {
                    await be.CreateModelAsync("TechColumn", compatibilityLevel);
                    await be.CreateTableAsync(sales, "human");
                    await be.CreateColumnAsync("table:" + sales, "Customer_SK", "Int64", "Customer_SK", "human");
                    await be.CreateColumnAsync("table:" + sales, "Amount", "Decimal", "Amount", "human");
                    await be.CreateTableAsync(customer, "human");
                    await be.CreateColumnAsync("table:" + customer, "Customer_SK", "Int64", "Customer_SK", "human");
                    await be.CreateColumnAsync("table:" + customer, "Customer Name", "String", "Customer Name", "human");
                }
            }, "TechColumn.bim");
            Assert.Equal(compatibilityLevel, await f.Sessions.Require().ReadAsync(m => m.Database.CompatibilityLevel));
            var key = $"column:{customer}/Customer_SK";
            switch (shape)
            {
                case "relationship": await e.CreateRelationshipAsync($"column:{sales}/Customer_SK", key, null, true, "human"); break;
                case "iskey": await Mutate(f, m => m.Tables[customer].Columns["Customer_SK"].IsKey = true); break;
                case "declared key part": await Mutate(f, m => SourceKeys.Mark(m.Tables[customer].Columns["Customer_SK"])); break;
                case "sort by": await Mutate(f, m => m.Tables[customer].Columns["Customer Name"].SortByColumn = m.Tables[customer].Columns["Customer_SK"]); break;
                case "hierarchy": await Mutate(f, m => m.Tables[customer].AddHierarchy("Customers", null, m.Tables[customer].Columns["Customer Name"], m.Tables[customer].Columns["Customer_SK"])); break;
                case "measure": await e.CreateMeasureAsync("table:" + customer, "Customers", $"DISTINCTCOUNT ( '{customer}'[Customer_SK] )", "human"); break;
                case "security filter": await Mutate(f, m => m.AddRole("Readers").RowLevelSecurity[m.Tables[customer]] = $"'{customer}'[Customer_SK] > 0"); break;
            }

            var finding = (await e.AiReadinessScanAsync()).Findings
                .Single(x => x.RuleId == "VIS-TECH-COLUMN" && x.ObjectRef == key);
            Assert.Contains("hide it", finding.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Best Practice Analyzer", finding.Message, StringComparison.Ordinal);

            // Do what the finding asks first, then compare with what BPA says about the hidden column.
            await Mutate(f, m => m.Tables[customer].Columns["Customer_SK"].IsHidden = true);
            // Below 1400 BPA cannot evaluate this rule at all (it reports it as unknown), so it raises nothing and the
            // text must not say it will; removal is still the true advice for a column nothing uses.
            var bpaRaises = removable && compatibilityLevel >= 1400;
            Assert.Equal(bpaRaises, (await FlaggedAsync(f, "UNNECESSARY_COLUMNS")).Contains(key));
            Assert.Equal(removable, finding.Message.Contains("remov", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(bpaRaises, finding.Message.Contains("Model quality (BPA)", StringComparison.Ordinal));
            Assert.Equal(!removable, finding.Message.Contains("Keep the column itself", StringComparison.Ordinal));
        }

        // ---- (d) a field named after its own table is not a name collision ------------------------------------

        private static async Task<string[]> CollisionFindingsAsync(DecisionFixture f) =>
            (await f.Engine.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "DAC-QNA-NAME-COLLISIONS")
                .Select(x => x.Message).ToArray();

        /// <summary>A star with a Staff dimension whose label column is "Staff Name", joined from a fact table.</summary>
        private async Task<DecisionFixture> StaffStarAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "staff-star", BuildStaffStarAsync, "StaffStar.bim");
            return f;
        }

        private static async Task BuildStaffStarAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("StaffStar", 1604);
            await e.CreateTableAsync("Timesheets", "human");
            await e.CreateColumnAsync("table:Timesheets", "Staff Number", "Int64", "Staff Number", "human");
            await e.CreateColumnAsync("table:Timesheets", "Hours", "Decimal", "Hours", "human");
            await e.CreateTableAsync("Staff", "human");
            await e.CreateColumnAsync("table:Staff", "Staff Number", "Int64", "Staff Number", "human");
            await e.CreateColumnAsync("table:Staff", "Staff Name", "String", "Staff Name", "human");
            await e.CreateRelationshipAsync("column:Timesheets/Staff Number", "column:Staff/Staff Number", null, true, "human");
            // The foreign key hidden, as the VIS-FK safe fix does, so its name is not a cross-table duplicate of the key.
            await Mutate(f, m => m.Tables["Timesheets"].Columns["Staff Number"].IsHidden = true);
        }

        /// <summary>DIM-PRIMARY-NAME asks for a dimension's label column to carry the table's own name ("Staff" in table
        /// Staff), which is Microsoft's naming guidance and exactly how Desktop's own AdventureWorks schema is saved
        /// (table Customer, column Customer[Customer]). DAC-QNA-NAME-COLLISIONS then failed the pair as a High, certain
        /// collision, and set_synonyms refused every write while it stood, so following one rule broke two others. A
        /// table and an object inside that same table are one thing to the linguistic schema, not two.</summary>
        [Fact]
        public async Task Following_the_dimension_naming_rule_opens_no_name_collision_and_synonyms_still_write()
        {
            using var f = await StaffStarAsync();
            var e = f.Engine;
            Assert.Contains((await e.AiReadinessScanAsync()).Findings, x => x.RuleId == "DIM-PRIMARY-NAME" && x.ObjectRef == "column:Staff/Staff Name");

            // Join, part 2 (Kane, 27 Sep): following DIM-PRIMARY-NAME is setting the row label (IsDefaultLabel), not a rename.
            await e.SetObjectPropertyAsync("column:Staff/Staff Name", "IsDefaultLabel", "true", "agent");
            await e.CreateLinguisticSchemaAsync(null, "agent");
            var card = await e.AiReadinessScanAsync();
            Assert.DoesNotContain(card.Findings, x => x.RuleId == "DIM-PRIMARY-NAME");
            Assert.Empty(await CollisionFindingsAsync(f));

            var table = await e.SetSynonymsAsync("table:Staff", new[] { "employee", "consultant" }, null, "agent");
            var column = await e.SetSynonymsAsync("column:Staff/Staff Name", new[] { "employee name" }, null, "agent");
            Assert.True(table.Changed && column.Changed);
            Assert.Null(column.Warning);
            Assert.Empty(await CollisionFindingsAsync(f));
        }

        /// <summary>The control for (d). Only an object inside the table it is named after is spared. A column of ANOTHER
        /// table named like a table still collides, in the rule and in the writer, and once a third object outside the
        /// table joins the pair the whole group is one finding that names all three.</summary>
        [Fact]
        public async Task A_name_shared_across_tables_still_collides_in_the_rule_and_the_writer()
        {
            using var f = await StaffStarAsync();
            var e = f.Engine;
            await e.RenameObjectAsync("column:Staff/Staff Name", "Staff", "agent");
            await e.CreateColumnAsync("table:Timesheets", "Staff", "String", "Staff", "human");
            await e.CreateLinguisticSchemaAsync(null, "agent");

            var finding = Assert.Single(await CollisionFindingsAsync(f));
            // Join, part 2 (Kane's ruling of 27 Sep): only visible FIELDS are compared, so the table 'Staff' is not a member.
            Assert.DoesNotContain("table 'Staff'", finding, StringComparison.Ordinal);
            Assert.Contains("column 'Staff'[Staff]", finding, StringComparison.Ordinal);
            Assert.Contains("column 'Timesheets'[Staff]", finding, StringComparison.Ordinal);

            // The writer compares a target with the objects that already carry schema entries, so give the table one.
            Assert.True((await e.SetSynonymsAsync("table:Staff", new[] { "employee" }, null, "agent")).Changed);
            // Kane's ruling of 27 Sep (feedback-lsdl): a clash is an answer-quality problem, never a failed write, so the writer does not refuse.
            Assert.True((await e.SetSynonymsAsync("column:Timesheets/Staff", new[] { "worker" }, null, "agent")).Changed);
        }

        /// <summary>The (d) pair under Kane's field-name ruling: a table and its own column are one word to the linguistic schema and
        /// no clash at all (the table is not a field), and the advisory has nothing to name.</summary>
        [Fact]
        public async Task A_table_and_its_own_column_are_not_a_name_clash_and_the_advisory_stays_quiet()
        {
            using var f = await StaffStarAsync();
            var e = f.Engine;
            await e.RenameObjectAsync("column:Staff/Staff Name", "Staff", "agent");
            await e.CreateLinguisticSchemaAsync(null, "agent");
            Assert.Empty(await CollisionFindingsAsync(f));
            Assert.True((await e.SetSynonymsAsync("table:Staff", new[] { "employee" }, null, "agent")).Changed);
            Assert.True((await e.SetSynonymsAsync("column:Staff/Staff", new[] { "employee name" }, null, "agent")).Changed);
            Assert.Null((await e.CreateLinguisticSchemaAsync(null, "agent")).Warning);
        }

        // ---- (e) creating the linguistic schema opens no second round of word work -------------------------------

        private static async Task<string[]> SynonymGapsAsync(DecisionFixture f) =>
            (await f.Engine.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "SYN-FIELD" || x.RuleId == "SYN-TABLE")
                .Select(x => x.RuleId + " " + x.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        /// <summary>Creating the linguistic schema is the fix for SYN-SCHEMA (High), and it switched on a finding for every
        /// visible field and table with no synonyms at once: 48 on the check's model, so one fix opened a second round of
        /// word work nobody had been shown. The fields had no synonyms before the schema existed either, so the synonym
        /// work is now counted from the start and creating the schema opens nothing new. The grade does not move: before
        /// the schema the Synonyms category already scored nothing.</summary>
        [Fact]
        public async Task Creating_the_linguistic_schema_opens_no_new_synonym_findings()
        {
            using var f = await StaffStarAsync();
            var before = await SynonymGapsAsync(f);
            Assert.Contains("SYN-TABLE table:Staff", before);
            Assert.Contains("SYN-FIELD column:Staff/Staff Name", before);
            var synonymsBefore = (await f.Engine.AiReadinessScanAsync()).Categories.Single(c => c.Category == "Synonyms").Score;
            Assert.Equal(0, synonymsBefore);

            await f.Engine.CreateLinguisticSchemaAsync(null, "agent");

            Assert.Equal(before, await SynonymGapsAsync(f));
            var card = await f.Engine.AiReadinessScanAsync();
            Assert.DoesNotContain(card.Findings, x => x.RuleId == "SYN-SCHEMA");
            Assert.True(card.Categories.Single(c => c.Category == "Synonyms").Score > synonymsBefore);
        }

        /// <summary>The honesty half of (e). The synonym work counted before the schema is real work the person can do:
        /// set_synonyms creates the schema itself. A model whose only schema is the legacy XML kind is left dormant,
        /// because SYN-LSDL-XML owns that case and the writer cannot patch it, and a model below compatibility level
        /// 1465 has no linguistic schema to write at all.</summary>
        [Fact]
        public async Task Synonym_work_counted_before_the_schema_can_be_done_and_legacy_schemas_stay_dormant()
        {
            using var f = await StaffStarAsync();
            Assert.True((await f.Engine.SetSynonymsAsync("table:Staff", new[] { "employee" }, null, "agent")).Changed);
            Assert.DoesNotContain("SYN-TABLE table:Staff", await SynonymGapsAsync(f));

            using var xml = await StaffStarAsync();
            await Mutate(xml, m =>
            {
                m.Culture = "en-US";
                var culture = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                culture.Content = "<LinguisticSchema />";
            });
            Assert.Empty(await SynonymGapsAsync(xml));
        }

        /// <summary>Creating the schema also switched on DAC-QNA-NAME-COLLISIONS, and on the check's model three High
        /// findings appeared at once. Those stay tied to the schema (they are about what the schema can hold), but the
        /// SYN-SCHEMA finding now stages them: before the person creates the schema it names the names to fix first and
        /// says the AI instructions come next, so nothing arrives unannounced.</summary>
        [Fact]
        public async Task The_schema_finding_names_the_collisions_the_schema_will_raise_before_it_exists()
        {
            using var f = await StaffStarAsync();
            await f.Engine.CreateColumnAsync("table:Timesheets", "Staff Name", "String", "Staff Name", "human");
            var schema = (await f.Engine.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "SYN-SCHEMA");
            Assert.Contains("1 name", schema.Message, StringComparison.Ordinal);
            Assert.Contains("column 'Staff'[Staff Name]", schema.Message, StringComparison.Ordinal);
            Assert.Contains("column 'Timesheets'[Staff Name]", schema.Message, StringComparison.Ordinal);
            Assert.Contains("AI instructions", schema.Message, StringComparison.Ordinal);
            Assert.Contains("AI data schema", schema.Message, StringComparison.Ordinal);   // creating the schema checks that too
            Assert.DoesNotContain("High", schema.Message, StringComparison.Ordinal);       // the Studio calls that level Error

            await f.Engine.CreateLinguisticSchemaAsync(null, "agent");
            var collision = Assert.Single(await CollisionFindingsAsync(f));
            Assert.Contains("column 'Timesheets'[Staff Name]", collision, StringComparison.Ordinal);

            using var clean = await StaffStarAsync();
            var quiet = (await clean.Engine.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "SYN-SCHEMA");
            Assert.DoesNotContain("same word", quiet.Message, StringComparison.Ordinal);
        }

        /// <summary>The verifier's D3, the coordinator's ruling. Counting the synonym work before a schema exists cost a
        /// model whose person had waived SYN-SCHEMA a letter grade, and reopened a finding for every field whose only fix,
        /// set_synonyms, creates the schema the person declined. A waiver is a recorded decision, so while SYN-SCHEMA is
        /// waived and no schema exists, SYN-FIELD and SYN-TABLE stay dormant and the Synonyms category scores what it did
        /// before the change: the waived finding counts as a pass. The controls: taking the waiver back counts the work
        /// again, and once a schema exists the waiver no longer holds the work back, because no fix would create it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_waived_schema_keeps_the_synonym_work_dormant_and_the_grade_where_it_was(bool ruleLevel)
        {
            using var f = await StaffStarAsync();
            var e = f.Engine;
            var schema = (await e.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "SYN-SCHEMA");
            Assert.NotEmpty(await SynonymGapsAsync(f));

            var target = ruleLevel ? null : schema.ObjectRef;
            await e.WaiveFindingAsync("air", "SYN-SCHEMA", target, "No Copilot questions on this model, so no linguistic schema on purpose.", "human");
            var card = await e.AiReadinessScanAsync();
            Assert.Empty(await SynonymGapsAsync(f));
            Assert.True(card.Findings.Single(x => x.RuleId == "SYN-SCHEMA").Waived);
            Assert.Equal(100, card.Categories.Single(c => c.Category == "Synonyms").Score);
            Assert.DoesNotContain("fieldsWithSynonyms", card.Coverage.Keys);

            await e.UnwaiveFindingAsync("air", "SYN-SCHEMA", target, "human");
            Assert.NotEmpty(await SynonymGapsAsync(f));

            await e.WaiveFindingAsync("air", "SYN-SCHEMA", target, "No Copilot questions on this model, so no linguistic schema on purpose.", "human");
            await e.CreateLinguisticSchemaAsync(null, "agent");
            Assert.Contains("SYN-TABLE table:Staff", await SynonymGapsAsync(f));
        }

        // ---- (c) the whole-number rule does not raise a format that already has thousands separators ----------

        /// <summary>The assistant set "#,##0" on its counts and "#,##0.0" on its hours. "Whole numbers should be formatted
        /// with thousands separators" raised both, and one press of Fix all rewrote them to "#,0", which for the hours
        /// also dropped the decimal the assistant chose. Both already show thousands separators, the thing the rule asks
        /// for, exactly like the rule's own "#,0" and "#,0.0", so they are not raised and nothing is offered to rewrite
        /// them. Only whether they are raised changed here: how any fix writes a format is the V1d fix's, on its lane.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("#,##0")]
        [InlineData("#,##0.0")]
        public async Task The_whole_number_rule_does_not_raise_a_format_that_already_has_thousands_separators(string chosen)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "formats-hours", async b =>
            {
                await b.Engine.CreateModelAsync("Formats", 1604);
                await b.Engine.CreateTableAsync("Sales", "human");
                await b.Engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
                await b.Engine.CreateMeasureAsync("table:Sales", "Hours", "SUM ( Sales[Amount] )", "human");
            }, "FormatsHours.bim");
            await Mutate(f, m => m.Tables["Sales"].Measures["Hours"].FormatString = chosen);

            Assert.DoesNotContain("measure:Sales/Hours", await FlaggedAsync(f, "INTEGER_FORMATTING"));
            await f.Engine.BpaFixAllAsync("human");
            Assert.Equal(chosen, await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Hours"].FormatString));
        }

        /// <summary>The control for (c). Every other format the round-7 rule raised is still raised, so the change is those
        /// two formats and nothing wider: an empty format, a bare whole number, and a format with decimals and no house
        /// style. The house style itself is never raised. Only the empty format's fill is asserted, because filling a blank
        /// value is the one write both lanes agree on.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("0", true)]
        [InlineData("##0", true)]
        [InlineData("0.00", true)]
        [InlineData("#,##0.00", true)]
        [InlineData("#,##0;-#,##0", true)]
        [InlineData("#,0", false)]
        [InlineData("#,0.0", false)]
        public async Task The_whole_number_rule_still_raises_every_other_plain_number_format(string existing, bool raised)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "formats-orders", async b =>
            {
                await b.Engine.CreateModelAsync("Formats", 1604);
                await b.Engine.CreateTableAsync("Sales", "human");
                await b.Engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
                await b.Engine.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            }, "FormatsOrders.bim");
            await Mutate(f, m => m.Tables["Sales"].Measures["Orders"].FormatString = existing);

            Assert.Equal(raised, (await FlaggedAsync(f, "INTEGER_FORMATTING")).Contains("measure:Sales/Orders"));
            if (!string.IsNullOrWhiteSpace(existing)) return;
            if (existing.Length > 0)
            {
                // Join, part 2 (m11/feedback-rules, V1f): a format of only blank spaces counts as blank for the advice but is never written
                // over, so the fix refuses it in words and leaves what the person typed.
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.BpaFixAsync("INTEGER_FORMATTING", "measure:Sales/Orders", "agent"));
                Assert.Equal(existing, await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Orders"].FormatString));
                return;
            }
            Assert.True((await f.Engine.BpaFixAsync("INTEGER_FORMATTING", "measure:Sales/Orders", "agent")).Changed);
            Assert.Equal("#,0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Orders"].FormatString));
            Assert.DoesNotContain("measure:Sales/Orders", await FlaggedAsync(f, "INTEGER_FORMATTING"));
        }
    }
}
