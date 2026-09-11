using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Next-release catalog completion batch 7. Five deterministic rules folded in from the Microsoft
    /// semantic-model-authoring provenance backlog and the design spec's own unimplemented Copilot ceiling.
    /// Every rule proves three states: a violation, a clean applicable population, and a dormant model with no
    /// applicable population (so the rule can never inflate its category on a model it does not apply to).
    /// </summary>
    public sealed class ReadinessCatalogCompletionBatch7Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static RuleEvaluation Eval(SessionManager sessions, string id) =>
            ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync(string name)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync(name, 1604);
            return (engine, sessions);
        }

        // ---- AISCHEMA-DEP-MISSING ------------------------------------------------------------------------

        [Fact]
        public async Task Ai_schema_dependency_rule_covers_violation_clean_and_dormant_populations()
        {
            var (engine, sessions) = await ModelAsync("Batch7AiSchema");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
                await engine.CreateColumnAsync(sales, "Cost", "Decimal", "Cost", "human");
                await engine.CreateColumnAsync(sales, "Notes", "String", "Notes", "human");
                var totalAmount = await engine.CreateMeasureAsync(sales, "Total Amount", "SUM ( Sales[Amount] )", "human");
                await engine.CreateMeasureAsync(sales, "Total Cost", "SUM ( Sales[Cost] )", "human");

                // Dormant: no linguistic schema at all, so there is no curated AI data schema to validate.
                var noSchema = Eval(sessions, "AISCHEMA-DEP-MISSING");
                Assert.Equal(0, noSchema.Applicable);
                Assert.Empty(noSchema.Violations);

                // Still dormant: a seeded schema that excludes nothing is the uncurated default (DAC-AI-DATA-SCHEMA owns that).
                await engine.EnableQnaAsync(null, "human");
                var seeded = Eval(sessions, "AISCHEMA-DEP-MISSING");
                Assert.Equal(0, seeded.Applicable);
                Assert.Empty(seeded.Violations);

                // Clean: a curated schema that excludes a column no included measure depends on.
                await engine.SetAiDataSchemaAsync("column:Sales/Notes", false, null, "human");
                var clean = Eval(sessions, "AISCHEMA-DEP-MISSING");
                Assert.Equal(2, clean.Applicable);   // both visible measures are still included and evaluated
                Assert.Empty(clean.Violations);

                // Violation: [Total Amount] stays in the schema while the column it reads is excluded.
                await engine.SetAiDataSchemaAsync("column:Sales/Amount", false, null, "human");
                var violation = Eval(sessions, "AISCHEMA-DEP-MISSING");
                Assert.Equal(2, violation.Applicable);
                var finding = Assert.Single(violation.Violations);
                Assert.Equal("Total Amount", finding.ObjectName);
                Assert.Contains("Amount", finding.Message, StringComparison.Ordinal);

                // Excluding the dependent measure too removes it from the evaluated population, not by weakening the check.
                await engine.SetAiDataSchemaAsync(totalAmount, false, null, "human");
                var excludedMeasure = Eval(sessions, "AISCHEMA-DEP-MISSING");
                Assert.Equal(1, excludedMeasure.Applicable);
                Assert.Empty(excludedMeasure.Violations);
            }
        }

        [Fact]
        public async Task Ai_schema_dependency_rule_ignores_model_hidden_dependents()
        {
            var (engine, sessions) = await ModelAsync("Batch7AiSchemaHidden");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var key = await engine.CreateColumnAsync(sales, "Sales Key", "Int64", "Sales Key", "human");
                await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
                await engine.CreateMeasureAsync(sales, "Row Count", "COUNTROWS ( DISTINCT ( Sales[Sales Key] ) )", "human");
                await engine.SetColumnMetadataAsync(key, true, null, null, null, "human");

                await engine.EnableQnaAsync(null, "human");
                // Power BI auto-excludes model-hidden fields, so a hidden dependent is not a curation mistake.
                await engine.SetAiDataSchemaAsync("column:Sales/Sales Key", false, null, "human");
                var hiddenDependent = Eval(sessions, "AISCHEMA-DEP-MISSING");
                Assert.Equal(1, hiddenDependent.Applicable);
                Assert.Empty(hiddenDependent.Violations);
            }
        }

        [Fact]
        public async Task Ai_schema_dependency_rule_resolves_a_bare_reference_after_a_dax_keyword()
        {
            var (engine, sessions) = await ModelAsync("Batch7AiSchemaKeyword");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
                await engine.CreateMeasureAsync(sales, "Base Amount", "SUM ( Sales[Amount] )", "human");
                // A bare [Measure] reference whose PRECEDING token is the RETURN keyword, not a table name. This is the
                // ordinary shape of any VAR-using measure, so a resolver that reads every preceding Word as a table
                // qualifier drops the dependency entirely instead of reporting it.
                await engine.CreateMeasureAsync(sales, "Guarded Amount", "VAR one = 1 RETURN [Base Amount] * one", "human");

                await engine.EnableQnaAsync(null, "human");
                await engine.SetAiDataSchemaAsync("measure:Sales/Base Amount", false, null, "human");

                var ev = Eval(sessions, "AISCHEMA-DEP-MISSING");
                Assert.Equal(1, ev.Applicable);   // [Base Amount] is excluded, so only [Guarded Amount] is evaluated
                var finding = Assert.Single(ev.Violations);
                Assert.Equal("Guarded Amount", finding.ObjectName);
                Assert.Contains("[Base Amount]", finding.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Ai_schema_dependency_resolver_keeps_qualified_comment_and_variable_handling()
        {
            var (engine, sessions) = await ModelAsync("Batch7AiSchemaResolver");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
                await engine.CreateColumnAsync(sales, "Cost", "Decimal", "Cost", "human");
                var other = await engine.CreateTableAsync("Other", "human");
                await engine.CreateColumnAsync(other, "Amount", "Decimal", "Amount", "human");
                await engine.CreateColumnAsync(other, "Cost", "Decimal", "Cost", "human");

                // Qualified in both spellings: the quoted-table form and the bare-word form must both still resolve.
                await engine.CreateMeasureAsync(sales, "Quoted Cost", "SUM ( 'Sales'[Cost] )", "human");
                await engine.CreateMeasureAsync(sales, "Word Cost", "SUM ( Sales[Cost] )", "human");
                // A name that only appears inside a comment or a string literal is not a dependency.
                await engine.CreateMeasureAsync(sales, "Commented", "-- Sales[Cost]\nSUM ( Sales[Amount] ) + 0 * LEN ( \"Sales[Cost]\" )", "human");
                // A table VARIABLE qualifier is not a model table. This measure lives on Sales and reads 'Other'[Cost]
                // through a variable, so resolving t[Cost] as an unqualified name would charge it with the EXCLUDED
                // 'Sales'[Cost] it never touches, which would be a false High finding on valid DAX.
                await engine.CreateMeasureAsync(sales, "Var Qualified", "VAR t = SUMMARIZE ( Other, Other[Cost] ) RETURN SUMX ( t, t[Cost] )", "human");

                await engine.EnableQnaAsync(null, "human");
                await engine.SetAiDataSchemaAsync("column:Sales/Cost", false, null, "human");

                var ev = Eval(sessions, "AISCHEMA-DEP-MISSING");
                Assert.Equal(4, ev.Applicable);
                Assert.Equal(
                    new[] { "Quoted Cost", "Word Cost" },
                    ev.Violations.Select(v => v.ObjectName).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            }
        }

        [Fact]
        public async Task Ai_schema_dependency_rule_excludes_measures_on_a_model_hidden_table()
        {
            var (engine, sessions) = await ModelAsync("Batch7AiSchemaHiddenTable");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
                await engine.CreateColumnAsync(sales, "Cost", "Decimal", "Cost", "human");
                await engine.CreateMeasureAsync(sales, "Total Amount", "SUM ( Sales[Amount] )", "human");

                // A model-HIDDEN table. Its measure is not hidden itself, so a check that only reads Measure.IsHidden
                // counts it as part of the AI data schema and charges it with a dependency it should never be asked about.
                var internals = await engine.CreateTableAsync("Internal", "human");
                await engine.CreateMeasureAsync(internals, "Internal Cost", "SUM ( Sales[Cost] )", "human");
                await engine.SetObjectPropertyAsync(internals, "IsHidden", "true", "human");

                await engine.EnableQnaAsync(null, "human");
                await engine.SetAiDataSchemaAsync("column:Sales/Cost", false, null, "human");

                var ev = Eval(sessions, "AISCHEMA-DEP-MISSING");
                Assert.Equal(1, ev.Applicable);   // [Total Amount] only; [Internal Cost] is off the visible surface
                Assert.Empty(ev.Violations);
            }
        }

        [Fact]
        public async Task Ai_schema_dependency_rule_honours_table_level_ai_exclusion()
        {
            var (engine, sessions) = await ModelAsync("Batch7AiSchemaTableExclusion");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
                await engine.CreateMeasureAsync(sales, "Total Amount", "SUM ( Sales[Amount] )", "human");
                // Reads a field of a table that is excluded WHOLE, so the dependency is just as absent from the
                // curated schema as a field-level exclusion would make it.
                await engine.CreateMeasureAsync(sales, "Total Rawval", "SUM ( Staging[Rawval] )", "human");

                var staging = await engine.CreateTableAsync("Staging", "human");
                await engine.CreateColumnAsync(staging, "Rawval", "Decimal", "Rawval", "human");
                await engine.CreateMeasureAsync(staging, "Staging Total", "SUM ( Staging[Rawval] )", "human");

                await engine.EnableQnaAsync(null, "human");
                // set_ai_data_schema accepts a TABLE ref and writes one table entity carrying Visibility:Hidden.
                var set = await engine.SetAiDataSchemaAsync(staging, false, null, "human");
                Assert.True(set.Changed);

                var ev = Eval(sessions, "AISCHEMA-DEP-MISSING");
                // [Staging Total] lives on the excluded table, so it is not in the schema and is not evaluated.
                Assert.Equal(2, ev.Applicable);
                var finding = Assert.Single(ev.Violations);
                Assert.Equal("Total Rawval", finding.ObjectName);
                Assert.Contains("'Staging'[Rawval]", finding.Message, StringComparison.Ordinal);
            }
        }

        // ---- SYN-ENTITY-ORPHAN ---------------------------------------------------------------------------

        [Fact]
        public async Task Orphan_linguistic_entity_rule_covers_violation_clean_and_dormant_populations()
        {
            var (engine, sessions) = await ModelAsync("Batch7Lsdl");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var amount = await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");

                // Dormant: no linguistic schema, so there are no entities to resolve.
                var noSchema = Eval(sessions, "SYN-ENTITY-ORPHAN");
                Assert.Equal(0, noSchema.Applicable);
                Assert.Empty(noSchema.Violations);

                // Clean: every bound entity still resolves to a live model object.
                await engine.EnableQnaAsync(null, "human");
                await engine.SetAiDataSchemaAsync("column:Sales/Amount", false, null, "human");
                var clean = Eval(sessions, "SYN-ENTITY-ORPHAN");
                Assert.True(clean.Applicable >= 1);
                Assert.Empty(clean.Violations);

                // Violation: delete the bound column and the entity binding is left dangling in the LSDL.
                var applicableBefore = clean.Applicable;
                await engine.DeleteObjectAsync(amount, "human");
                var violation = Eval(sessions, "SYN-ENTITY-ORPHAN");
                Assert.Equal(applicableBefore, violation.Applicable);
                var finding = Assert.Single(violation.Violations);
                Assert.Contains("Amount", finding.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Orphan_linguistic_entity_rule_stays_dormant_on_a_malformed_entity_binding()
        {
            var (engine, sessions) = await ModelAsync("Batch7LsdlMalformed");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
                await engine.EnableQnaAsync(null, "human");

                // Valid JSON whose entities carry a non-object Definition / Binding. JsonElement.TryGetProperty
                // throws on a non-object, so an unguarded read would fail the whole scan; the contract is dormancy.
                await sessions.Current.MutateAsync("human", "malformed lsdl", m => m.Cultures[0].Content =
                    "{\"Entities\":{\"a\":{\"Definition\":null},\"b\":{\"Definition\":\"nope\"},"
                    + "\"c\":{\"Definition\":[1,2]},\"d\":{\"Binding\":\"nope\"},\"e\":42}}");

                var orphan = Eval(sessions, "SYN-ENTITY-ORPHAN");
                Assert.Equal(0, orphan.Applicable);
                Assert.Empty(orphan.Violations);

                // The whole scan still completes: no rule throws on the malformed schema.
                var card = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
                Assert.DoesNotContain(card.Findings, f => f.RuleId == "SYN-ENTITY-ORPHAN");
                Assert.DoesNotContain(card.Findings, f => f.RuleId == "AISCHEMA-DEP-MISSING");
            }
        }

        /// <summary>Correction 4: two orphans in ONE culture used to file two findings that both carried the
        /// culture's own object ref, so their finding identity , and therefore their waiver identity, which is
        /// (system, ruleId, objectRef) , was identical. Waiving one silently waived the other. The rule is now an
        /// honest culture-level aggregate: population = cultures, one finding per culture, and the text enumerates
        /// every orphan binding so nothing is lost by aggregating. A second culture stays isolated.</summary>
        [Fact]
        public async Task Orphan_linguistic_entity_findings_are_one_per_culture_and_enumerate_every_orphan()
        {
            var (engine, sessions) = await ModelAsync("Batch7LsdlGranularity");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
                await engine.EnableQnaAsync(null, "human");

                // en-US: one live binding plus TWO orphans (a deleted column and a deleted table).
                // fr-FR: one live binding plus ONE orphan, to prove the aggregate does not leak across cultures.
                const string EnUs = "{\"Entities\":{"
                    + "\"sales.amount\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Sales\",\"ConceptualProperty\":\"Amount\"}}},"
                    + "\"sales.cost\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Sales\",\"ConceptualProperty\":\"Cost\"}}},"
                    + "\"archive\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Archive\"}}}}}";
                const string FrFr = "{\"Entities\":{"
                    + "\"sales.amount\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Sales\",\"ConceptualProperty\":\"Amount\"}}},"
                    + "\"sales.marge\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Sales\",\"ConceptualProperty\":\"Marge\"}}}}}";
                await sessions.Current.MutateAsync("human", "two linguistic cultures", m =>
                {
                    m.Cultures[0].Content = EnUs;
                    var fr = m.AddTranslation("fr-FR");
                    fr.Content = FrFr;
                });

                var ev = Eval(sessions, "SYN-ENTITY-ORPHAN");

                // Population is CULTURES, matching the finding granularity: two linguistic cultures carry bindings.
                Assert.Equal(2, ev.Applicable);
                Assert.Equal(2, ev.Violations.Count);

                // Two orphans in en-US collapse into ONE finding that names BOTH of them.
                var enUs = Assert.Single(ev.Violations.Where(f => f.ObjectName == "en-US"));
                Assert.Contains("'Sales'[Cost]", enUs.Message, StringComparison.Ordinal);
                Assert.Contains("table 'Archive'", enUs.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Marge", enUs.Message, StringComparison.Ordinal);

                // The second culture reports only its OWN orphan.
                var frFr = Assert.Single(ev.Violations.Where(f => f.ObjectName == "fr-FR"));
                Assert.Contains("'Sales'[Marge]", frFr.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Cost", frFr.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Archive", frFr.Message, StringComparison.Ordinal);

                // The two findings now carry DISTINCT object refs, so a waiver on one cannot cover the other.
                Assert.Equal(2, ev.Violations.Select(f => f.ObjectRef).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
        }

        // ---- LIMIT-DAX-LENGTH ----------------------------------------------------------------------------

        [Fact]
        public async Task Dax_length_ceiling_rule_covers_violation_clean_and_dormant_populations()
        {
            var (engine, sessions) = await ModelAsync("Batch7DaxLength");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");

                // Dormant: no DAX-bearing object exists at all.
                var noDax = Eval(sessions, "LIMIT-DAX-LENGTH");
                Assert.Equal(0, noDax.Applicable);
                Assert.Empty(noDax.Violations);

                // Clean: an expression exactly at the documented 5,000-character ceiling is not a breach.
                var atCeiling = "SUM ( Sales[Amount] )" + new string(' ', 5000 - "SUM ( Sales[Amount] )".Length);
                Assert.Equal(5000, atCeiling.Length);
                var measure = await engine.CreateMeasureAsync(sales, "Long Measure", atCeiling, "human");
                var clean = Eval(sessions, "LIMIT-DAX-LENGTH");
                Assert.Equal(0, clean.Applicable);   // presence design: no artificial clean pass to inflate CopilotLimits
                Assert.Empty(clean.Violations);

                // Violation: one character past the ceiling.
                await engine.SetDaxAsync(measure, atCeiling + " ", "human");
                var violation = Eval(sessions, "LIMIT-DAX-LENGTH");
                Assert.Equal(1, violation.Applicable);
                var finding = Assert.Single(violation.Violations);
                Assert.Contains("Long Measure", finding.Message, StringComparison.Ordinal);
                Assert.Contains("5,001", finding.Message, StringComparison.Ordinal);
            }
        }

        // ---- DESC-HIERARCHY ------------------------------------------------------------------------------

        [Fact]
        public async Task Hierarchy_description_rule_covers_violation_clean_and_dormant_populations()
        {
            var (engine, sessions) = await ModelAsync("Batch7HierarchyDesc");
            using (engine)
            {
                var date = await engine.CreateTableAsync("Date", "human");
                await engine.CreateColumnAsync(date, "Year", "Int64", "Year", "human");
                await engine.CreateColumnAsync(date, "Month", "String", "Month", "human");

                // Dormant: no hierarchy exists.
                var noHierarchy = Eval(sessions, "DESC-HIERARCHY");
                Assert.Equal(0, noHierarchy.Applicable);
                Assert.Empty(noHierarchy.Violations);

                // Violation: a visible hierarchy carries no description for Copilot/Q&A grounding.
                var hierarchy = await engine.CreateHierarchyAsync("table:Date", "Calendar", new[] { "Year", "Month" }, "human");
                var violation = Eval(sessions, "DESC-HIERARCHY");
                Assert.Equal(1, violation.Applicable);
                var finding = Assert.Single(violation.Violations);
                Assert.Equal("Calendar", finding.ObjectName);

                // Clean: the same population, now described.
                // set_description accepts measures/tables/columns/perspectives only, so the clean state is written
                // straight onto the model (the rule reads TOM, not the writer).
                Assert.NotNull(hierarchy);
                await sessions.Current.MutateAsync("human", "describe hierarchy",
                    m => m.Tables["Date"].Hierarchies["Calendar"].Description = "Calendar drill path from year to month.");
                var clean = Eval(sessions, "DESC-HIERARCHY");
                Assert.Equal(1, clean.Applicable);
                Assert.Empty(clean.Violations);
            }
        }

        // ---- DIM-PRIMARY-NAME ----------------------------------------------------------------------------

        [Fact]
        public async Task Dimension_primary_name_rule_covers_violation_clean_and_dormant_populations()
        {
            var (engine, sessions) = await ModelAsync("Batch7PrimaryName");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var salesKey = await engine.CreateColumnAsync(sales, "Product Key", "Int64", "Product Key", "human");
                var product = await engine.CreateTableAsync("Product", "human");
                var productKey = await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");

                // Dormant: no dimension table exposes a "<Table> Name" label column yet.
                var noCandidate = Eval(sessions, "DIM-PRIMARY-NAME");
                Assert.Equal(0, noCandidate.Applicable);
                Assert.Empty(noCandidate.Violations);

                var productName = await engine.CreateColumnAsync(product, "Product Name", "String", "Product Name", "human");
                // Still dormant until the graph proves Product is the one-side dimension of a relationship.
                var unrelated = Eval(sessions, "DIM-PRIMARY-NAME");
                Assert.Equal(0, unrelated.Applicable);
                Assert.Empty(unrelated.Violations);

                // Violation: the dimension's label column is "Product Name" and nothing is named "Product".
                await engine.CreateRelationshipAsync(salesKey, productKey, null, true, "human");
                var violation = Eval(sessions, "DIM-PRIMARY-NAME");
                Assert.Equal(1, violation.Applicable);
                var finding = Assert.Single(violation.Violations);
                // The finding names the LABEL COLUMN, which is the object the advertised AiContent rename edits.
                // It used to name the table, which no fix touches (see the plan test below).
                Assert.Equal("Product Name", finding.ObjectName);
                Assert.Equal("column:Product/Product Name", finding.ObjectRef);
                Assert.Contains("Dimension 'Product'", finding.Message, StringComparison.Ordinal);
                Assert.Contains("Product Name", finding.Message, StringComparison.Ordinal);

                // Clean: the same population, with the label renamed to match the table.
                await engine.RenameObjectAsync(productName, "Product", "human");
                var clean = Eval(sessions, "DIM-PRIMARY-NAME");
                Assert.Equal(1, clean.Applicable);
                Assert.Empty(clean.Violations);
            }
        }

        /// <summary>Correction 3: the rule's own comment claims "the same deterministic graph slice REL-SNOWFLAKE
        /// uses", but the relationship filter omitted REL-SNOWFLAKE's Many-to-One cardinality test, so a one-to-one
        /// or many-to-many edge made its To-side table a scored "dimension". Neither is the fact-to-dimension slice,
        /// so neither may move a Naming score through this rule.</summary>
        [Fact]
        public async Task Dimension_primary_name_rule_scores_only_the_many_to_one_dimension_slice()
        {
            var (engine, sessions) = await ModelAsync("Batch7PrimaryNameCardinality");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var salesCustomer = await engine.CreateColumnAsync(sales, "Customer Key", "Int64", "Customer Key", "human");
                var salesRegion = await engine.CreateColumnAsync(sales, "Region Key", "Int64", "Region Key", "human");

                // Both tables are label-column violations on their own: "<Table> Name" and no bare "<Table>" field.
                var customer = await engine.CreateTableAsync("Customer", "human");
                var customerKey = await engine.CreateColumnAsync(customer, "Customer Key", "Int64", "Customer Key", "human");
                await engine.CreateColumnAsync(customer, "Customer Name", "String", "Customer Name", "human");
                var region = await engine.CreateTableAsync("Region", "human");
                var regionKey = await engine.CreateColumnAsync(region, "Region Key", "Int64", "Region Key", "human");
                await engine.CreateColumnAsync(region, "Region Name", "String", "Region Name", "human");

                await engine.CreateRelationshipAsync(salesCustomer, customerKey, null, true, "human");
                await engine.CreateRelationshipAsync(salesRegion, regionKey, null, true, "human");

                // Baseline: as many-to-one edges, BOTH are in the slice and both violate.
                var manyToOne = Eval(sessions, "DIM-PRIMARY-NAME");
                Assert.Equal(2, manyToOne.Applicable);
                Assert.Equal(2, manyToOne.Violations.Count);

                // Retarget the SAME two edges to one-to-one and many-to-many. Nothing about the label columns
                // changed, so any movement here comes from the cardinality slice alone.
                await sessions.Current.MutateAsync("human", "retarget cardinality", m =>
                {
                    var rels = m.Relationships.OfType<SingleColumnRelationship>().ToList();
                    var toCustomer = rels.Single(r => r.ToTable.Name == "Customer");
                    toCustomer.FromCardinality = RelationshipEndCardinality.One;
                    toCustomer.ToCardinality = RelationshipEndCardinality.One;
                    var toRegion = rels.Single(r => r.ToTable.Name == "Region");
                    toRegion.FromCardinality = RelationshipEndCardinality.Many;
                    toRegion.ToCardinality = RelationshipEndCardinality.Many;
                });

                var offSlice = Eval(sessions, "DIM-PRIMARY-NAME");
                Assert.Equal(0, offSlice.Applicable);
                Assert.Empty(offSlice.Violations);

                // Applicable = 0 keeps the rule out of the category entirely, so the Naming score is untouched.
                var card = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
                Assert.DoesNotContain(card.Findings, f => f.RuleId == "DIM-PRIMARY-NAME");
            }
        }

        /// <summary>Correction 5: the rule advertises <c>FixKind.AiContent</c>, but <c>MapAiContent</c> had no case
        /// for it, so the advertised fix produced NO plan item; and the finding targeted the TABLE while the
        /// intended fix renames the LABEL COLUMN. The finding now targets the column, and the existing AI rename
        /// mapping carries it into the plan , still <c>needs_content</c> at <c>rename</c> risk, so approval and the
        /// rename-safe application path are unchanged.</summary>
        [Fact]
        public async Task Dimension_primary_name_rename_reaches_the_plan_on_the_label_column()
        {
            var (engine, sessions) = await ModelAsync("Batch7PrimaryNamePlan");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var salesKey = await engine.CreateColumnAsync(sales, "Product Key", "Int64", "Product Key", "human");
                var product = await engine.CreateTableAsync("Product", "human");
                var productKey = await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");
                await engine.CreateColumnAsync(product, "Product Name", "String", "Product Name", "human");
                await engine.CreateRelationshipAsync(salesKey, productKey, null, true, "human");

                // The finding names the column that actually gets renamed, not the table.
                var finding = Assert.Single(Eval(sessions, "DIM-PRIMARY-NAME").Violations);
                Assert.Equal("Product Name", finding.ObjectName);
                Assert.Equal("column:Product/Product Name", finding.ObjectRef);

                var plan = await engine.ProposePlanAsync(null, includeAi: true, maxAiItems: 50, origin: "human");
                var item = Assert.Single(plan.Items.Where(i => i.RuleId == "DIM-PRIMARY-NAME"));

                // The intended rename, on the column, through the existing AI rename mapping.
                Assert.Equal("rename", item.Kind);
                Assert.Equal("column:Product/Product Name", item.ObjectRef);
                Assert.Equal("name", item.Target);
                Assert.Equal("Product Name", item.Before);
                Assert.Contains("Product", item.Title, StringComparison.Ordinal);

                // Approval semantics preserved: AI-authored content, NOT auto-approved, and carried at rename risk
                // so it goes through the rename-safe path rather than a blind property write.
                Assert.Equal("needs_content", item.Status);
                Assert.Equal("rename", item.Risk);
                Assert.Equal("ai", item.Source);
                Assert.Null(item.After);

                // Approval semantics preserved through authoring: "rename" is an OPT-IN kind, so filling the value
                // leaves the item PROPOSED (never auto-approved) and still needs an explicit human approval.
                var authored = await engine.SetPlanItemAsync(item.Id, "Product", null, "human");
                Assert.Equal("proposed", authored.Items.Single(i => i.Id == item.Id).Status);
                Assert.Equal("Product", authored.Items.Single(i => i.Id == item.Id).After);
                var approved = await engine.SetPlanItemAsync(item.Id, null, true, "human");
                Assert.Equal("approved", approved.Items.Single(i => i.Id == item.Id).Status);

                // Application semantics preserved: it applies through the shared rename seam and the model ends up
                // with the bare table name, which is exactly what clears the finding.
                var report = await engine.ApplyPlanAsync(null, "human");
                Assert.Equal("applied", report.Items.Single(r => r.Id == item.Id).Status);
                var after = Eval(sessions, "DIM-PRIMARY-NAME");
                Assert.Equal(1, after.Applicable);
                Assert.Empty(after.Violations);
                Assert.Contains(sessions.Current.Model.Tables["Product"].Columns, c => c.Name == "Product");
            }
        }

        /// <summary>Correction 5, second half: one property gets one plan item, so when NAME-COLUMN and
        /// DIM-PRIMARY-NAME both want to rename the same codey label column the shared dedup key
        /// (objectRef + target) keeps a single rename , correctly, since two renames of one object in one batch is
        /// not a plan. It used to drop the losing finding's GUIDANCE along with its item, which is precisely where
        /// DIM-PRIMARY-NAME's point (rename it to the TABLE's name, not just to something clearer) was lost. The
        /// deduped finding now merges its message into the surviving item's rationale.</summary>
        [Fact]
        public async Task Dimension_primary_name_guidance_survives_dedup_against_a_competing_rename_rule()
        {
            var (engine, sessions) = await ModelAsync("Batch7PrimaryNameDedup");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var salesKey = await engine.CreateColumnAsync(sales, "Currency Key", "Int64", "Currency Key", "human");
                var currency = await engine.CreateTableAsync("Currency", "human");
                var currencyKey = await engine.CreateColumnAsync(currency, "Currency Key", "Int64", "Currency Key", "human");
                // "CurrencyName" is BOTH the "<Table>Name" label shape and a camelCase-hump codey name, so
                // DIM-PRIMARY-NAME and NAME-COLUMN both fire on this one column.
                await engine.CreateColumnAsync(currency, "CurrencyName", "String", "CurrencyName", "human");
                await engine.CreateRelationshipAsync(salesKey, currencyKey, null, true, "human");

                var card = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
                var competing = card.Findings
                    .Where(f => f.ObjectRef == "column:Currency/CurrencyName" && (f.RuleId == "NAME-COLUMN" || f.RuleId == "DIM-PRIMARY-NAME"))
                    .ToArray();
                Assert.Equal(2, competing.Length);   // the collision this test exists for

                var plan = await engine.ProposePlanAsync(null, includeAi: true, maxAiItems: 50, origin: "human");
                var renames = plan.Items.Where(i => i.ObjectRef == "column:Currency/CurrencyName" && i.Target == "name").ToArray();

                // ONE item for the one property, with the ordinary rename contract intact.
                var item = Assert.Single(renames);
                Assert.Equal("rename", item.Kind);
                Assert.Equal("rename", item.Risk);
                Assert.Equal("needs_content", item.Status);
                Assert.Null(item.After);

                // Neither rule's guidance is lost: whichever rule owns the item, the rationale carries both messages.
                foreach (var f in competing)
                    Assert.Contains(f.Message, item.Rationale, StringComparison.Ordinal);
                Assert.Contains("has no field named 'Currency'", item.Rationale, StringComparison.Ordinal);
            }
        }

        // ---- integration through the shipped analyzer path ------------------------------------------------

        [Fact]
        public async Task Batch7_rules_are_built_in_and_reach_the_scorecard_through_the_analyzer()
        {
            var ids = new[] { "AISCHEMA-DEP-MISSING", "SYN-ENTITY-ORPHAN", "LIMIT-DAX-LENGTH", "DESC-HIERARCHY", "DIM-PRIMARY-NAME" };
            var builtIn = ReadinessRuleSet.Default().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in ids) Assert.Contains(id, builtIn);

            var (engine, sessions) = await ModelAsync("Batch7Analyzer");
            using (engine)
            {
                var date = await engine.CreateTableAsync("Date", "human");
                await engine.CreateColumnAsync(date, "Year", "Int64", "Year", "human");
                await engine.CreateColumnAsync(date, "Month", "String", "Month", "human");
                await engine.CreateHierarchyAsync("table:Date", "Calendar", new[] { "Year", "Month" }, "human");

                var card = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
                Assert.Contains(card.Findings, f => f.RuleId == "DESC-HIERARCHY");
                var descriptions = card.Categories.Single(c => c.Category == ReadinessCategory.Descriptions.ToString());
                Assert.True(descriptions.Applicable > 0);
            }
        }
    }
}
