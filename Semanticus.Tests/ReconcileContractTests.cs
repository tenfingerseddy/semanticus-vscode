using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// What the "Compare with source" drawer must keep VISIBLE and what it may hide behind Advanced. The split is not
    /// a UI opinion: it is what the engine actually refuses to run without, pinned here so the drawer and the engine
    /// cannot drift apart.
    ///
    /// The subtle one is grouping. The engine RUNS without it, so it is not required; but a grand-total-only run can
    /// never do better than "couldn't check", because a matching total cannot rule out the blank-row trap. A drawer
    /// that files grouping under Advanced without saying that is quietly offering a check that can never pass.
    /// </summary>
    [Collection("restore-root")]
    public sealed class ReconcileContractTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public ReconcileContractTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-reccontract-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private async Task<LocalEngine> ModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.CreateModelAsync("Rows", 1604);
            await engine.CreateImportTableAsync("Sales",
                "let Source = Sql.Database(\"fabric.example.com\", \"LH_Gold\"), t = Source{[Schema=\"dbo\",Item=\"fact_sales\"]}[Data] in t", "human");
            await engine.CreateMeasureAsync("Sales", "Total Rows", "COUNTROWS('Sales')", "human");
            return engine;
        }

        private static ReconcileRequest Valid() => new ReconcileRequest
        {
            MeasureRef = "measure:Sales/Total Rows",
            Sql = "SELECT 1",
            BlankPolicy = "zero",
            GroupBy = new[] { "'Sales'[n]" },
        };

        [Fact]
        public void The_contract_names_what_must_stay_visible_and_what_may_hide()
        {
            Assert.Equal(
                new[] { "measureRef", "sql", "blankPolicy" },
                ReconcileContract.Required.Select(f => f.Field).ToArray());

            Assert.Equal(
                new[] { "groupBy" },
                ReconcileContract.RequiredForAVerdict.Select(f => f.Field).ToArray());

            Assert.Equal(
                new[] { "sqlSourceId", "sqlGrandTotal", "filterDax", "toleranceAbsolute", "toleranceRelative", "maxRows" },
                ReconcileContract.Optional.Select(f => f.Field).ToArray());

            // Every field carries plain-words help the drawer can show, and none of it speaks engine jargon.
            foreach (var f in ReconcileContract.Required.Concat(ReconcileContract.RequiredForAVerdict).Concat(ReconcileContract.Optional))
            {
                Assert.False(string.IsNullOrWhiteSpace(f.Label), f.Field);
                Assert.False(string.IsNullOrWhiteSpace(f.Help), f.Field);
                Assert.DoesNotContain("—", f.Label + f.Help);
            }

            // Ten fields across the three lists, each named once.
            var all = ReconcileContract.Required.Concat(ReconcileContract.RequiredForAVerdict).Concat(ReconcileContract.Optional)
                .Select(f => f.Field).ToArray();
            Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(10, all.Length);

            // The four inline endpoint fields are deliberately ABSENT: a drawer should offer a saved source by name
            // (sqlSourceId), not ask a person to retype a server, a database, a sign-in mode and a tenant per check.
            // They still exist on the request so every check saved before named sources keeps working.
            foreach (var legacy in new[] { "server", "database", "authMode", "tenantId" })
                Assert.DoesNotContain(legacy, all);
        }

        [Fact]
        public async Task Each_required_field_is_actually_refused_when_it_is_missing()
        {
            using var engine = await ModelAsync();

            var noMeasure = Valid(); noMeasure.MeasureRef = null;
            Assert.Equal(ReconcileStatus.InputError, (await engine.ReconcileMeasureAsync(noMeasure)).Status);

            var noSql = Valid(); noSql.Sql = null;
            Assert.Equal(ReconcileStatus.InputError, (await engine.ReconcileMeasureAsync(noSql)).Status);

            var noPolicy = Valid(); noPolicy.BlankPolicy = null;
            var policyRun = await engine.ReconcileMeasureAsync(noPolicy);
            Assert.Equal(ReconcileStatus.InputError, policyRun.Status);
            Assert.Contains("blank policy is required", policyRun.Error);
        }

        [Fact]
        public async Task Grouping_is_optional_to_run_but_without_it_a_check_can_never_do_better_than_couldnt_check()
        {
            using var engine = await ModelAsync();

            var grandTotalOnly = Valid();
            grandTotalOnly.GroupBy = Array.Empty<string>();

            var run = await engine.ReconcileMeasureAsync(grandTotalOnly);

            // It is NOT refused as input: the engine runs it.
            Assert.NotEqual(ReconcileStatus.InputError, run.Status);
            // And it can never certify. Offline it stops earlier, which is the same honest ceiling.
            Assert.NotEqual(ReconcileStatus.Reconciled, run.Status);
            Assert.Contains("groupBy", ReconcileContract.RequiredForAVerdict.Select(f => f.Field));
        }
    }
}
