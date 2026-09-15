using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Which SQL a check actually reads. A named source wins; a check saved before named sources existed keeps its
    /// own coordinates and keeps working; a named source that has been removed is said out loud rather than quietly
    /// swapped for whatever the model happens to detect.
    /// </summary>
    [Collection("restore-root")]
    public sealed class SqlSourceResolutionTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public SqlSourceResolutionTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-sqlres-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static SqlSourceRecord SaveSource(string name = "Contoso warehouse")
            => SqlSourceRegistry.Save(null, name, "contoso-sql.database.windows.net", "Warehouse", "devicecode", "tenant-7");

        private static TestDefinition Def(string id, ReconcileRequest request) => new TestDefinition
        {
            Id = id, Kind = TestKinds.MeasureReconcile, Title = id, ParamsJson = TestSuiteStore.Serialize(request),
        };

        [Fact]
        public void A_named_source_wins_over_the_checks_own_inline_values()
        {
            var source = SaveSource();
            var request = new ReconcileRequest
            {
                SqlSourceId = source.Id,
                Server = "stale-sql.database.windows.net",
                Database = "StaleDb",
                AuthMode = "azcli",
                TenantId = "tenant-stale",
            };

            var resolved = SqlSources.Apply(request);

            Assert.Null(resolved.Error);
            Assert.True(resolved.FromSavedSource);
            Assert.Equal("Contoso warehouse", resolved.SourceName);
            // The request itself is rewritten, so nothing downstream has to know named sources exist.
            Assert.Equal("contoso-sql.database.windows.net", request.Server);
            Assert.Equal("Warehouse", request.Database);
            Assert.Equal("devicecode", request.AuthMode);
            Assert.Equal("tenant-7", request.TenantId);
        }

        [Fact]
        public void An_old_definition_with_inline_values_and_no_named_source_is_left_alone()
        {
            var request = new ReconcileRequest
            {
                Server = "legacy-sql.database.windows.net", Database = "LegacyDb", AuthMode = "azcli",
            };

            var resolved = SqlSources.Apply(request);

            Assert.Null(resolved.Error);
            Assert.False(resolved.FromSavedSource);
            Assert.Equal("legacy-sql.database.windows.net", request.Server);
            Assert.Equal("LegacyDb", request.Database);
            Assert.Equal("azcli", request.AuthMode);
        }

        [Fact]
        public void A_named_source_that_has_been_removed_is_an_error_not_a_silent_fallback()
        {
            var request = new ReconcileRequest
            {
                SqlSourceId = "sql-gone", Server = "stale-sql.database.windows.net", Database = "StaleDb",
            };

            var resolved = SqlSources.Apply(request);

            Assert.NotNull(resolved.Error);
            Assert.Contains("no longer saved", resolved.Error);
            Assert.DoesNotContain("—", resolved.Error);
            // The inline values are NOT adopted: running there would answer a different question than the one asked.
            Assert.Equal("stale-sql.database.windows.net", request.Server);
            Assert.False(resolved.FromSavedSource);
        }

        [Fact]
        public void List_tests_reports_which_source_each_check_uses()
        {
            var source = SaveSource();
            var defs = new List<TestDefinition>
            {
                Def("named", new ReconcileRequest { SqlSourceId = source.Id }),
                Def("inline", new ReconcileRequest { Server = "legacy-sql.database.windows.net", Database = "LegacyDb" }),
                Def("dangling", new ReconcileRequest { SqlSourceId = "sql-gone" }),
                Def("nothing", new ReconcileRequest()),
                new TestDefinition { Id = "value", Kind = TestKinds.MeasureValue, ParamsJson = "{}" },
            };

            var use = SqlSources.DescribeUse(defs);

            Assert.Equal(new[] { "named", "inline", "dangling" }, use.Select(u => u.DefId).ToArray());

            var named = use.Single(u => u.DefId == "named");
            Assert.Equal(source.Id, named.SqlSourceId);
            Assert.Equal("Contoso warehouse", named.SourceName);
            Assert.Equal("Warehouse", named.Database);
            Assert.False(named.Missing);

            var inline = use.Single(u => u.DefId == "inline");
            Assert.Null(inline.SqlSourceId);
            Assert.Null(inline.SourceName);
            Assert.Equal("legacy-sql.database.windows.net", inline.Server);
            Assert.False(inline.Missing);

            var dangling = use.Single(u => u.DefId == "dangling");
            Assert.True(dangling.Missing);
        }

        [Fact]
        public void Counting_the_checks_that_use_a_source_is_what_a_refused_delete_says_out_loud()
        {
            var source = SaveSource();
            var other = SaveSource("Other warehouse");
            var defs = new List<TestDefinition>
            {
                Def("a", new ReconcileRequest { SqlSourceId = source.Id }),
                Def("b", new ReconcileRequest { SqlSourceId = source.Id }),
                Def("c", new ReconcileRequest { SqlSourceId = other.Id }),
                Def("d", new ReconcileRequest { Server = "legacy-sql.database.windows.net" }),
            };

            Assert.Equal(2, SqlSources.CountChecksUsing(defs, source.Id));
            Assert.Equal(1, SqlSources.CountChecksUsing(defs, other.Id));
            Assert.Equal(0, SqlSources.CountChecksUsing(defs, "sql-gone"));
        }

        [Fact]
        public async Task A_saved_check_pointing_at_a_removed_source_reports_it_before_asking_for_a_connection()
        {
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.OpenAsync(TestModels.FindBim());
            var measure = (await engine.ListMeasuresAsync()).First();

            var outcome = await engine.TryTestAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile,
                Title = "points at a removed source",
                TargetRef = measure.Name,
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = measure.Name, SqlSourceId = "sql-gone", Sql = "SELECT 1", BlankPolicy = "zero",
                }),
            });

            Assert.Equal(Verdict.NotVerifiable, outcome.Verdict);
            Assert.Contains("no longer saved", outcome.Message);
        }

        [Fact]
        public async Task A_saved_check_pointing_at_a_live_source_gets_past_resolution()
        {
            var source = SaveSource();
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.OpenAsync(TestModels.FindBim());
            var measure = (await engine.ListMeasuresAsync()).First();

            var outcome = await engine.TryTestAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile,
                Title = "points at a saved source",
                TargetRef = measure.Name,
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = measure.Name, SqlSourceId = source.Id, Sql = "SELECT 1", BlankPolicy = "zero",
                }),
            });

            // Resolution succeeded, so the run reaches the ordinary offline stop instead of the source complaint.
            Assert.Equal(Verdict.NotVerifiable, outcome.Verdict);
            Assert.Contains("needs a live connection", outcome.Message);
            Assert.DoesNotContain("no longer saved", outcome.Message);
        }

        private async Task<LocalEngine> TinyModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.CreateModelAsync("Rows", 1604);
            // A table with NO SQL partition, so nothing can be derived from the model: only a named source
            // can supply the endpoint.
            await engine.CreateImportTableAsync("Nums", "let Source = #table({\"n\"}, {{1}}) in Source", "human");
            await engine.CreateMeasureAsync("Nums", "Total", "COUNTROWS('Nums')", "human");
            var measures = await engine.ListMeasuresAsync();
            Assert.Contains(measures, x => x.Name == "Total");
            return engine;
        }

        [Fact]
        public async Task A_direct_reconcile_call_resolves_a_named_source_too_so_the_field_is_never_ignored()
        {
            var source = SaveSource();
            using var engine = await TinyModelAsync();

            var run = await engine.ReconcileMeasureAsync(new ReconcileRequest
            {
                MeasureRef = "measure:Nums/Total", SqlSourceId = source.Id, Sql = "SELECT 1", BlankPolicy = "zero",
            });

            // The named source supplied the endpoint, so the run gets past "no endpoint" to the honest
            // offline stop. Without resolution here the sqlSourceId would be ignored and this would be
            // an InputError saying there is no Fabric SQL endpoint.
            Assert.DoesNotContain("No Fabric SQL endpoint", run.Error ?? "");
            Assert.Equal(ReconcileStatus.InsufficientCoverage, run.Status);
        }

        [Fact]
        public async Task A_direct_reconcile_call_naming_a_removed_source_says_so_in_plain_words()
        {
            using var engine = await TinyModelAsync();

            var run = await engine.ReconcileMeasureAsync(new ReconcileRequest
            {
                MeasureRef = "measure:Nums/Total", SqlSourceId = "sql-gone", Server = "stale-sql.database.windows.net",
                Database = "StaleDb", Sql = "SELECT 1", BlankPolicy = "zero",
            });

            Assert.Equal(ReconcileStatus.InputError, run.Status);
            Assert.Contains("no longer saved", run.Error);
            Assert.DoesNotContain("\u2014", run.Error);
        }
    }
}
