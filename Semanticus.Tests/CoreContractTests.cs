using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Core engine CONTRACTS that are easy to silently regress into a no-op: the one-way compatibility-level
    /// guard (a lower would corrupt a model that uses newer features) and the net-zero delete (an absent ref must not
    /// bump the revision or broadcast). Pinned so a refactor can't quietly weaken them.</summary>
    public sealed class CoreContractTests : IAsyncLifetime
    {
        private SessionManager _sessions = null!;
        private LocalEngine _engine = null!;

        public async Task InitializeAsync()
        {
            _sessions = new SessionManager();
            _engine = new LocalEngine(_sessions);
            await _engine.OpenAsync(TestModels.FindBim());
        }

        public Task DisposeAsync() { _engine.Dispose(); return Task.CompletedTask; }

        [Fact]
        public async Task Compatibility_level_is_a_one_way_upgrade()
        {
            Assert.True((await _engine.SetCompatibilityLevelAsync(1604, "human")).Changed);   // raising works
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _engine.SetCompatibilityLevelAsync(1500, "human"));                      // LOWERING is refused
            Assert.False((await _engine.SetCompatibilityLevelAsync(1604, "human")).Changed);   // still 1604 → no-op
        }

        [Fact]
        public async Task Deleting_an_absent_object_is_a_net_zero_no_op()
        {
            // Resolve-first: an unresolvable ref must NOT bump the revision or broadcast (the mutate path otherwise
            // always increments + publishes), so a delete of nothing is truly nothing.
            Assert.False((await _engine.DeleteObjectAsync("measure:NoSuchTable/NoSuchMeasure", "human")).Changed);
        }

        [Fact]
        public async Task Deleting_a_column_a_measure_references_succeeds_and_leaves_the_dependent_DAX_dangling()
        {
            // Contract pin (QA backlog #9): FormulaFixup rewrites DAX on RENAME only — it does NOT run on delete.
            // Deleting a column a measure references therefore (a) SUCCEEDS (does not block) and (b) does NOT cascade
            // to or rewrite the dependent measure, which survives carrying a now-DANGLING reference. If a future
            // change adds fixup-on-delete, this test fails loudly so the change is a deliberate, reviewed decision.
            var table = (await _engine.ListMeasuresAsync()).First().Table;
            var tq = "'" + table.Replace("'", "''") + "'";

            var colRef = await _engine.CreateColumnAsync("table:" + table, "Del_DepCol", "Int64", null, "agent");
            Assert.Equal("column:" + table + "/Del_DepCol", colRef);

            const string measureName = "Del_DependentMeasure";
            var measureDax = $"SUM({tq}[Del_DepCol])";
            await _engine.CreateMeasureAsync("table:" + table, measureName, measureDax, "agent");

            // Sanity: the dependency is real (the measure is a dependent of the column).
            Assert.Contains(await _engine.GetDependentsAsync(colRef), d => d.Name == measureName);

            // The DELETE succeeds even though the column is referenced — it neither blocks nor cascades.
            var del = await _engine.DeleteObjectAsync(colRef, "agent");
            Assert.True(del.Changed);
            Assert.DoesNotContain(await _engine.ListColumnsAsync(), c => c.Name == "Del_DepCol");

            // The dependent measure STILL EXISTS and its DAX is UNCHANGED — a dangling reference, NOT auto-rewritten.
            var survivor = (await _engine.ListMeasuresAsync()).FirstOrDefault(m => m.Name == measureName);
            Assert.NotNull(survivor);
            Assert.Equal(measureDax, survivor!.Expression);
            Assert.Contains("[Del_DepCol]", survivor.Expression);
        }
    }
}
