using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// F-249 (Codex on pull request 392): cherry_pick and the merge into the open model copied a measure, calculated
    /// column or calculation item straight onto the target, so an expression naming an object the target lacks was
    /// committed while get_verified_mode promised every DAX write refuses unknown references. Each copied expression
    /// now runs the shared write guard before any setter, one item at a time (the paths' existing per-item contract).
    /// The sweep for other unguarded DAX setters found two more user-supplied fragments written unchecked: the start
    /// and end of generate_date_table and a raw date column for generate_time_intelligence (tests at the bottom).
    /// </summary>
    public sealed class DaxWriteCopyGuardTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private const string Col = "Copy Guard Col";
        private const string Measure = "Copy Guard Measure";
        private const string DependentCol = "Copy Guard Dependent";
        private const string Group = "Copy Guard Group";
        private const string Item = "Copy Guard Item";

        // The source has a calculated column, a measure and a calculated column that both name it, and a calculation
        // item that names it too. The target is the same model without that column (the group exists, empty), so every
        // one of those expressions references a column the target lacks.
        private static async Task<(LocalEngine engine, SessionManager sessions, string sourceDir, string table)> OpenAsync(bool withGroup = false)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            await engine.OpenAsync(TestModels.FindBim());
            var table = (await engine.ListMeasuresAsync()).First().Table;
            await engine.CreateCalculatedColumnAsync("table:" + table, Col, "1", "human");
            await engine.CreateMeasureAsync("table:" + table, Measure, $"SUM('{table}'[{Col}])", "human");
            await engine.CreateCalculatedColumnAsync("table:" + table, DependentCol, $"'{table}'[{Col}] + 1", "human");
            if (withGroup)
            {
                await engine.SetCompatibilityLevelAsync(1600, "human");
                var g = await engine.CreateCalculationGroupAsync(Group, "human");
                await engine.CreateCalculationItemAsync(g, Item, $"SELECTEDMEASURE() + SUM('{table}'[{Col}])", "human");
            }
            var sourceDir = Path.Combine(Path.GetTempPath(), "sem-copy-guard-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            await engine.SaveAsync(sourceDir, "TMDL", false, "human");
            await engine.OpenAsync(TestModels.FindBim());
            if (withGroup)
            {
                await engine.SetCompatibilityLevelAsync(1600, "human");
                await engine.CreateCalculationGroupAsync(Group, "human");
            }
            return (engine, sessions, sourceDir, table);
        }

        private static async Task<bool> HasMeasure(LocalEngine e, string name) => (await e.ListMeasuresAsync()).Any(m => m.Name == name);

        [Fact]
        public async Task Cherry_pick_refuses_a_measure_that_names_a_column_the_target_lacks()
        {
            var (engine, _, dir, table) = await OpenAsync();
            using (engine)
            {
                try
                {
                    var src = new ModelRef { Kind = "file", Path = dir };
                    var r = await engine.CherryPickAsync(src, new[] { $"measure:{table}/{Measure}" }, true, commit: true, "agent");
                    Assert.True(r.Applied);
                    Assert.Equal(0, r.Count);
                    var fail = Assert.Single(r.FailedRefs);
                    Assert.Contains(Col, fail);
                    Assert.Contains("not valid DAX", fail);
                    Assert.False(await HasMeasure(engine, Measure));
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Cherry_pick_refuses_a_calculated_column_that_names_a_column_the_target_lacks()
        {
            var (engine, _, dir, table) = await OpenAsync();
            using (engine)
            {
                try
                {
                    var src = new ModelRef { Kind = "file", Path = dir };
                    var r = await engine.CherryPickAsync(src, new[] { $"column:{table}/{DependentCol}" }, true, commit: true, "agent");
                    Assert.Equal(0, r.Count);
                    Assert.Contains(Col, Assert.Single(r.FailedRefs));
                    Assert.DoesNotContain((await engine.ListColumnsAsync()).Where(c => c.Table == table).Select(c => c.Name), n => n == DependentCol);
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Cherry_pick_refuses_a_calculation_item_that_names_a_column_the_target_lacks()
        {
            var (engine, _, dir, _) = await OpenAsync(withGroup: true);
            using (engine)
            {
                try
                {
                    var src = new ModelRef { Kind = "file", Path = dir };
                    var r = await engine.CherryPickAsync(src, new[] { $"calcitem:{Group}/{Item}" }, true, commit: true, "agent");
                    Assert.Equal(0, r.Count);
                    Assert.Contains(Col, Assert.Single(r.FailedRefs));
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
        }

        // Preview must say what commit will do: the refused copy is listed under "cannot", not as a copy.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Cherry_pick_preview_lists_the_refused_copy_as_cannot()
        {
            var (engine, _, dir, table) = await OpenAsync();
            using (engine)
            {
                try
                {
                    var src = new ModelRef { Kind = "file", Path = dir };
                    var r = await engine.CherryPickAsync(src, new[] { $"measure:{table}/{Measure}" }, true, commit: false, "agent");
                    Assert.Equal(0, r.Count);
                    Assert.Contains(Col, Assert.Single(r.FailedRefs));
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
        }

        // One bad item fails alone; the column it needs, picked in the same batch AFTER it, still lets it land.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Cherry_pick_copies_a_measure_whose_column_arrives_in_the_same_batch()
        {
            var (engine, _, dir, table) = await OpenAsync();
            using (engine)
            {
                try
                {
                    var src = new ModelRef { Kind = "file", Path = dir };
                    var refs = new[] { $"measure:{table}/{Measure}", $"column:{table}/{Col}" };
                    var pv = await engine.CherryPickAsync(src, refs, true, commit: false, "agent");
                    Assert.Equal(2, pv.Count);
                    Assert.Empty(pv.FailedRefs);
                    var r = await engine.CherryPickAsync(src, refs, true, commit: true, "agent");
                    Assert.Equal(2, r.Count);
                    Assert.Empty(r.FailedRefs);
                    Assert.True(await HasMeasure(engine, Measure));
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Model_diff_merge_refuses_a_measure_that_names_a_column_the_target_lacks()
        {
            var (engine, _, dir, table) = await OpenAsync();
            using (engine)
            {
                try
                {
                    var selected = new[] { $"measure:{table}/{Measure}" };
                    var pv = await McpTools.ApplyModelDiff(engine, sourceFile: dir, selectedRefs: selected, commit: false);
                    Assert.Equal(0, pv.Count);
                    Assert.Contains(Col, Assert.Single(pv.FailedRefs));
                    var r = await McpTools.ApplyModelDiff(engine, sourceFile: dir, selectedRefs: selected, commit: true, confirmToken: pv.ConfirmToken);
                    Assert.Equal(0, r.Count);
                    var fail = Assert.Single(r.FailedRefs);
                    Assert.Contains(Col, fail);
                    Assert.Contains("not valid DAX", fail);
                    Assert.False(await HasMeasure(engine, Measure));
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Model_diff_merge_copies_a_measure_whose_column_arrives_in_the_same_batch()
        {
            var (engine, _, dir, table) = await OpenAsync();
            using (engine)
            {
                try
                {
                    var selected = new[] { $"measure:{table}/{Measure}", $"column:{table}/{Col}", $"column:{table}/{DependentCol}" };
                    var pv = await McpTools.ApplyModelDiff(engine, sourceFile: dir, selectedRefs: selected, commit: false);
                    Assert.Equal(3, pv.Count);
                    var r = await McpTools.ApplyModelDiff(engine, sourceFile: dir, selectedRefs: selected, commit: true, confirmToken: pv.ConfirmToken);
                    Assert.Equal(3, r.Count);
                    Assert.Empty(r.FailedRefs);
                    Assert.True(await HasMeasure(engine, Measure));
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
        }

        // ---- F-249 sweep: user-supplied DAX fragments the generators wrote without the guard ----

        private static async Task<LocalEngine> TiModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.CreateModelAsync("GuardGen", 1604);
            var t = await engine.CreateTableAsync("Sales", "agent");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "agent");
            await engine.CreateTableAsync("Date", "agent");
            await engine.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "agent");
            await engine.CreateMeasureAsync(t, "Total Amount", "SUM ( Sales[Amount] )", "agent");
            return engine;
        }

        [Fact]
        public async Task Generate_date_table_refuses_a_start_that_names_a_column_the_model_lacks()
        {
            using var engine = await TiModelAsync();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.GenerateDateTableAsync("Calendar", "MIN('Sales'[No Such Col])", null, markAsDate: false, "agent"));
            Assert.Contains("No Such Col", ex.Message);
            Assert.DoesNotContain((await engine.ListColumnsAsync()).Select(c => c.Table), n => n == "Calendar");
        }

        [Fact]
        public async Task Generate_time_intelligence_refuses_a_date_column_the_model_lacks()
        {
            using var engine = await TiModelAsync();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.GenerateTimeIntelligenceAsync("measure:Sales/Total Amount", "'Date'[No Such Col]", null, null, "agent"));
            Assert.Contains("No Such Col", ex.Message);
            Assert.False(await HasMeasure(engine, "Total Amount YTD"));
        }

        // The guard must not cost the ordinary raw form: a real 'Table'[Column] still generates.
        [Fact]
        public async Task Generate_time_intelligence_accepts_a_real_raw_date_column()
        {
            using var engine = await TiModelAsync();
            var r = await engine.GenerateTimeIntelligenceAsync("measure:Sales/Total Amount", "'Date'[Date]", null, null, "agent");
            Assert.Contains(r.Created, c => c.Name == "Total Amount YTD");
        }
    }
}
