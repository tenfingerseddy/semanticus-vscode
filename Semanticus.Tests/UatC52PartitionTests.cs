using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>UAT C5.2: new tables, renamed calculated tables, and opened models keep honest partitions
    /// (D-050, D-063).</summary>
    public sealed class UatC52PartitionTests
    {
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> NewModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("C52", 1604);
            return (engine, sessions);
        }

        // D-050: New Table on an opened model must not invent an empty provider data source.
        [Fact]
        public async Task Opened_model_new_table_is_an_M_partition_without_a_provider_source()
        {
            var dir = Path.Combine(Path.GetTempPath(), "semanticus-c52-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var created = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
                {
                    await created.CreateModelAsync("B Edit", 1604);
                    await created.CreateCalculatedTableAsync("Existing Calc", "{1}", "human");
                    await created.SaveAsync(dir, "TMDL");
                }

                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(dir);

                var tableRef = await McpTools.CreateTable(engine, "B Table");
                Assert.Equal("table:B Table", tableRef);

                var part = (await engine.ListPartitionsAsync(tableRef)).Single();
                Assert.Equal("M", part.SourceType);
                Assert.True(string.IsNullOrEmpty(part.DataSource));

                var names = await sessions.Require().ReadAsync(m => m.DataSources.Select(d => d.Name).ToArray());
                Assert.DoesNotContain("New Provider Data Source", names);
                Assert.Empty(names);
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        }

        [Fact]
        public async Task Fresh_model_new_table_is_an_M_partition_without_a_provider_source()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                var tableRef = await engine.CreateTableAsync("B Table", "human");
                var part = (await engine.ListPartitionsAsync(tableRef)).Single();
                Assert.Equal("M", part.SourceType);
                Assert.True(string.IsNullOrEmpty(part.DataSource));
                Assert.Empty(await sessions.Require().ReadAsync(m => m.DataSources.Select(d => d.Name).ToArray()));
            }
        }

        // D-063: renaming a calculated table also renames the matching default partition.
        [Fact]
        public async Task Calculated_table_rename_renames_the_matching_partition()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateCalculatedTableAsync("New Calculated Table", "{1}", "human");
                Assert.Equal("New Calculated Table",
                    (await engine.ListPartitionsAsync("table:New Calculated Table")).Single().Name);

                var renamed = await engine.RenameObjectAsync("table:New Calculated Table", "B Calc Table", "human");
                Assert.True(renamed.Changed);
                Assert.Equal("table:B Calc Table", renamed.NewRef);

                var part = (await engine.ListPartitionsAsync("table:B Calc Table")).Single();
                Assert.Equal("B Calc Table", part.Name);

                var mcp = await McpTools.CreateCalculatedTable(engine, "New Calculated Table", "{2}");
                Assert.Equal("table:New Calculated Table", mcp);
                var mcpRenamed = await McpTools.RenameObject(engine, mcp, "Agent Calc");
                Assert.Equal("table:Agent Calc", mcpRenamed.NewRef);
                Assert.Equal("Agent Calc", (await engine.ListPartitionsAsync(mcpRenamed.NewRef)).Single().Name);

                var stillNamed = await sessions.Require().ReadAsync(m => m.Tables["B Calc Table"].Partitions.Single().Name);
                Assert.Equal("B Calc Table", stillNamed);
            }
        }
    }
}
