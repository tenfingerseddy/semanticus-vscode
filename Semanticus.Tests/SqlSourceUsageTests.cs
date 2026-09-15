using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Connections shows which saved checks and which table mappings use a SQL source. It used to get that
    /// by calling list_tests and list_table_mappings, and both are Pro now: list_tests returns whole check
    /// definitions and list_table_mappings returns saved verdicts and row counts, so neither may become free.
    /// list_sql_source_usage is the shared free projection that keeps the page working, and it returns the usage and
    /// nothing else.</summary>
    public sealed class SqlSourceUsageTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        [Fact]
        public async Task The_free_tier_may_read_source_usage()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(false));
            await engine.OpenAsync(TestModels.FindBim());
            var usage = await engine.ListSqlSourceUsageAsync();
            Assert.NotNull(usage);   // must not throw the feature gate
        }

        [Fact]
        public async Task The_free_tier_is_still_refused_the_two_Pro_listings_it_replaces()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(false));
            await engine.OpenAsync(TestModels.FindBim());
            await Assert.ThrowsAsync<EntitlementException>(() => engine.ListTestDefinitionsAsync());
            await Assert.ThrowsAsync<EntitlementException>(() => engine.ListTableSourceMappingsAsync());
        }

        /// <summary>The contract, asserted as a shape rather than as prose: id, name, the names of the checks using
        /// it and the names of the mappings using it. Nothing else. A definition, an expected value, a verdict or a
        /// row count appearing here would be the Pro data leaking back out through the free door.</summary>
        [Fact]
        public void The_projection_carries_the_usage_and_nothing_else()
        {
            var names = typeof(SqlSourceUsage).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "CheckTitles", "ChecksUsing", "Id", "MappedTables", "Name", "TableMappingsUsing" }, names);
            Assert.Equal(typeof(string), typeof(SqlSourceUsage).GetProperty("Id").PropertyType);
            Assert.Equal(typeof(string), typeof(SqlSourceUsage).GetProperty("Name").PropertyType);
            Assert.Equal(typeof(int), typeof(SqlSourceUsage).GetProperty("ChecksUsing").PropertyType);
            Assert.Equal(typeof(int), typeof(SqlSourceUsage).GetProperty("TableMappingsUsing").PropertyType);
            Assert.Equal(typeof(string[]), typeof(SqlSourceUsage).GetProperty("CheckTitles").PropertyType);
            Assert.Equal(typeof(string[]), typeof(SqlSourceUsage).GetProperty("MappedTables").PropertyType);

            // Same field names SqlSourceDeleteResult already uses, so the page reads one shape for both answers.
            foreach (var shared in new[] { "ChecksUsing", "TableMappingsUsing", "CheckTitles", "MappedTables" })
                Assert.Equal(typeof(SqlSourceDeleteResult).GetProperty(shared).PropertyType,
                             typeof(SqlSourceUsage).GetProperty(shared).PropertyType);
        }

        [Fact]
        public void It_is_on_both_doors_and_on_a_shelf()
        {
            Assert.Contains("list_sql_source_usage", ProFeatureMapTests.CompiledOperationNames());
            Assert.Contains(ProFeatureMapTests.RpcEntries(), m => m.Name == "listSqlSourceUsage");
            Assert.True(OpTaxonomy.TryGet("list_sql_source_usage", out var question, out var shelf));
            Assert.Equal("Open a model and connect", question);
            Assert.Equal("Connections and targets", shelf);
        }

        [Fact]
        public void It_is_free_on_both_doors()
        {
            Assert.Contains("list_sql_source_usage", FeatureMap.FreeOperations);
            Assert.Contains("listSqlSourceUsage", FeatureMap.FreeRpcMethods);
            Assert.Contains("ListSqlSourceUsageAsync", FeatureMap.FreeEngineMethods);
        }

        /// <summary>No open model is a plain empty answer, not a throw: Connections renders before a model is open.</summary>
        [Fact]
        public async Task With_no_model_open_it_answers_with_no_usage_rather_than_throwing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(false));
            var usage = await engine.ListSqlSourceUsageAsync();
            Assert.NotNull(usage);
            Assert.All(usage, u => Assert.Empty(u.CheckTitles));
            Assert.All(usage, u => Assert.Empty(u.MappedTables));
            Assert.All(usage, u => Assert.Equal(0, u.ChecksUsing));
            Assert.All(usage, u => Assert.Equal(0, u.TableMappingsUsing));
        }
    }
}
