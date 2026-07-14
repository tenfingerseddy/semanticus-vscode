using System;
using System.IO;
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
    /// Next-release catalog completion batch 2. These rules already shipped but their research rows still said
    /// missing/partial. The tests make the row-to-rule upgrade executable: violation, clean and dormant/advisory
    /// behavior is pinned before the catalog arithmetic moves.
    /// </summary>
    public sealed class ReadinessCatalogBatch4Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static async Task<(LocalEngine engine, SessionManager sessions)> BaseModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("CatalogBatch4", 1604);
            var table = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(table, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(table, "Customer Name", "String", "Customer Name", "human");
            await engine.CreateMeasureAsync(table, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            return (engine, sessions);
        }

        private static RuleEvaluation Eval(SessionManager sessions, string id)
        {
            PrepForAiReader.Invalidate(sessions.Current.Model);
            return ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);
        }

        private static string TempDir(string name)
        {
            var path = Path.Combine(Path.GetTempPath(), "semanticus-readiness-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        [Fact]
        public async Task Qna_storage_rules_cover_violation_clean_and_dormant_states()
        {
            var (import, importSessions) = await BaseModelAsync();
            using (import)
            {
                Assert.Empty(Eval(importSessions, "DAC-QNA-STORAGE").Violations); // Import: advisory dormant
                Assert.Equal(0, Eval(importSessions, "DAC-QNA-DISABLED").Applicable); // no readable PBIP switch
            }

            var dlSessions = new SessionManager();
            using (var directLake = new LocalEngine(dlSessions, new Fake()))
            {
                await directLake.CreateModelAsync("DirectLake", 1604);
                await directLake.CreateDirectLakeTableAsync("Lake Sales", "sales", null, null, "human");
                var unknown = Eval(dlSessions, "DAC-QNA-STORAGE");
                Assert.Equal(0, unknown.Applicable); // context advisory, never a scoring pass
                Assert.Single(unknown.Violations);
                Assert.Equal(0, Eval(dlSessions, "DAC-QNA-DISABLED").Applicable);
            }

            var dir = TempDir("qna-clean");
            var diskSessions = new SessionManager();
            try
            {
                var copy = Path.Combine(dir, "AdventureWorks.bim");
                File.Copy(TestModels.FindBim(), copy);
                File.WriteAllText(Path.Combine(dir, "definition.pbism"), "{ \"settings\": { \"qnaEnabled\": true } }");
                using var enabled = new LocalEngine(diskSessions, new Fake(), dir);
                await enabled.OpenAsync(copy);
                await diskSessions.Current.MutateAsync("human", "mark partitions DirectQuery for the readiness fixture", m =>
                {
                    foreach (var p in m.AllPartitions) p.Mode = ModeType.DirectQuery;
                });
                Assert.Empty(Eval(diskSessions, "DAC-QNA-STORAGE").Violations);
                var actual = Eval(diskSessions, "DAC-QNA-DISABLED");
                Assert.Equal(1, actual.Applicable);
                Assert.Empty(actual.Violations);
            }
            finally
            {
                diskSessions.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public async Task Calculation_group_description_rules_partition_missing_incomplete_clean_and_dormant()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, Eval(sessions, "DAC-CALC-GROUP").Applicable);
                Assert.Equal(0, Eval(sessions, "DESC-CALCGROUP-ITEMS").Applicable);

                var group = await engine.CreateCalculationGroupAsync("Time Intelligence", "human");
                await engine.CreateCalculationItemAsync(group, "YTD", "SELECTEDMEASURE()", "human");
                await engine.CreateCalculationItemAsync(group, "PY", "SELECTEDMEASURE()", "human");
                Assert.Single(Eval(sessions, "DAC-CALC-GROUP").Violations);
                Assert.Equal(0, Eval(sessions, "DESC-CALCGROUP-ITEMS").Applicable);

                await engine.SetDescriptionAsync(group, "Applies YTD calculations.", "human");
                Assert.Empty(Eval(sessions, "DAC-CALC-GROUP").Violations);
                Assert.Single(Eval(sessions, "DESC-CALCGROUP-ITEMS").Violations);

                await engine.SetDescriptionAsync(group, "Items: YTD and PY. Use these for time comparisons.", "human");
                var complete = Eval(sessions, "DESC-CALCGROUP-ITEMS");
                Assert.Equal(1, complete.Applicable);
                Assert.Empty(complete.Violations);
            }
        }

        [Fact]
        public async Task Description_length_rules_cover_measures_tables_columns_and_dormant_models()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, Eval(sessions, "DESC-LONG").Applicable);
                Assert.Equal(0, Eval(sessions, "DESC-LONG-OBJECT").Applicable);

                await engine.SetDescriptionAsync("measure:Sales/Total Sales", new string('M', 201), "human");
                Assert.Single(Eval(sessions, "DESC-LONG").Violations);
                await engine.SetDescriptionAsync("measure:Sales/Total Sales", "Total sales amount for the selected context.", "human");
                Assert.Empty(Eval(sessions, "DESC-LONG").Violations);

                await engine.SetDescriptionAsync("table:Sales", new string('T', 201), "human");
                Assert.Single(Eval(sessions, "DESC-LONG-OBJECT").Violations);
                await engine.SetDescriptionAsync("table:Sales", "Sales transactions and their customer attributes.", "human");
                await engine.SetDescriptionAsync("column:Sales/Customer Name", new string('C', 201), "human");
                Assert.Single(Eval(sessions, "DESC-LONG-OBJECT").Violations);
                await engine.SetDescriptionAsync("column:Sales/Customer Name", "Customer display name.", "human");
                var clean = Eval(sessions, "DESC-LONG-OBJECT");
                Assert.Equal(2, clean.Applicable);
                Assert.Empty(clean.Violations);
            }
        }

        [Fact]
        public async Task Data_type_and_sort_rules_cover_violation_clean_and_dormant_populations()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, Eval(sessions, "FMT-DATATYPE").Applicable);
                Assert.Equal(0, Eval(sessions, "FMT-SORTBY").Applicable);

                var date = await engine.CreateColumnAsync("table:Sales", "Order Date", "String", "Order Date", "human");
                Assert.Single(Eval(sessions, "FMT-DATATYPE").Violations);
                await engine.SetColumnDataTypeAsync(date, "DateTime", "human");
                Assert.Empty(Eval(sessions, "FMT-DATATYPE").Violations);

                var priority = await engine.CreateColumnAsync("table:Sales", "Priority", "String", "Priority", "human");
                await engine.CreateColumnAsync("table:Sales", "Priority Sort", "Int64", "Priority Sort", "human");
                var unsorted = Eval(sessions, "FMT-SORTBY");
                Assert.Equal(1, unsorted.Applicable);
                Assert.Single(unsorted.Violations);
                await McpTools.SetSortByColumn(engine, priority, "Priority Sort");
                var sorted = Eval(sessions, "FMT-SORTBY");
                Assert.Equal(1, sorted.Applicable);
                Assert.Empty(sorted.Violations);

                await engine.CreateColumnAsync("table:Sales", "Status", "String", "Status", "human");
                await engine.CreateColumnAsync("table:Sales", "Status Sort", "Int64", "Status Sort", "human");
                await engine.CreateColumnAsync("table:Sales", "Status Order", "Int64", "Status Order", "human");
                var ambiguous = Eval(sessions, "FMT-SORTBY");
                Assert.DoesNotContain(ambiguous.Violations, f => f.ObjectName == "Status");
            }
        }

        [Fact]
        public async Task Table_scope_rule_is_advisory_and_dormant_below_the_threshold()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Empty(Eval(sessions, "LIMIT-DATAAGENT-TABLES").Violations);
                for (var i = 0; i < 25; i++) await engine.CreateTableAsync("Dimension " + i, "human");
                var scope = Eval(sessions, "LIMIT-DATAAGENT-TABLES");
                Assert.Equal(0, scope.Applicable);
                Assert.Single(scope.Violations);
            }
        }
    }
}
