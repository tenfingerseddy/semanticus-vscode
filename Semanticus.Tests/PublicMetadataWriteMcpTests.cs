using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Direct public MCP-door contracts for T111's high-risk metadata writers. Every success must be one
    /// agent-attributed commit on the shared timeline; every bad argument must fail without a revision or broadcast.</summary>
    public sealed class PublicMetadataWriteMcpTests
    {
        [Fact]
        public async Task Set_data_category_is_public_broadcast_undoable_and_failure_atomic()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.CreateModelAsync("McpDataCategory", 1604);
            var table = await McpTools.CreateTable(engine, "Geo");
            var city = await McpTools.CreateColumn(engine, table, "City", "String");

            var (result, change) = await CaptureAsync(sessions, () => McpTools.SetDataCategory(engine, city, "City"));
            AssertAgentCommit(result, change, city, "DataCategory");
            Assert.Equal("City", Column(await McpTools.ListColumns(engine), city).DataCategory);

            Assert.False((await McpTools.SetDataCategory(engine, city, "City")).Changed);

            await AssertNoCommitAsync(sessions, async () =>
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SetDataCategory(engine, table, "Country"));
                Assert.Contains("not a column", ex.Message);
            });
            Assert.Equal("City", Column(await McpTools.ListColumns(engine), city).DataCategory);

            await engine.UndoAsync("human");
            Assert.True(string.IsNullOrEmpty(Column(await McpTools.ListColumns(engine), city).DataCategory));
            await McpTools.RedoChange(engine);
            Assert.Equal("City", Column(await McpTools.ListColumns(engine), city).DataCategory);
        }

        [Fact]
        public async Task Set_sort_by_column_is_public_broadcast_undoable_and_rejects_unknown_or_self_columns()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.CreateModelAsync("McpSortBy", 1604);
            var table = await McpTools.CreateTable(engine, "Date");
            var month = await McpTools.CreateColumn(engine, table, "Month Name", "String");
            await McpTools.CreateColumn(engine, table, "Month Number", "Int64");

            var (result, change) = await CaptureAsync(sessions, () => McpTools.SetSortByColumn(engine, month, "Month Number"));
            AssertAgentCommit(result, change, month, "SortByColumn");
            Assert.Equal("Month Number", Column(await McpTools.ListColumns(engine), month).SortByColumn);

            Assert.False((await McpTools.SetSortByColumn(engine, month, "Month Number")).Changed);

            await AssertNoCommitAsync(sessions, async () =>
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SetSortByColumn(engine, month, "Missing"));
                Assert.Contains("not found", ex.Message);
            });
            await AssertNoCommitAsync(sessions, async () =>
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SetSortByColumn(engine, month, "Month Name"));
                Assert.Contains("cannot sort by itself", ex.Message);
            });
            Assert.Equal("Month Number", Column(await McpTools.ListColumns(engine), month).SortByColumn);

            await engine.UndoAsync("human");
            Assert.Null(Column(await McpTools.ListColumns(engine), month).SortByColumn);
            await McpTools.RedoChange(engine);
            Assert.Equal("Month Number", Column(await McpTools.ListColumns(engine), month).SortByColumn);
        }

        [Fact]
        public async Task Set_relationship_crossfilter_is_public_broadcast_undoable_and_rejects_invalid_directions()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.CreateModelAsync("McpCrossfilter", 1604);
            var fact = await McpTools.CreateTable(engine, "Sales");
            var lookup = await McpTools.CreateTable(engine, "Customer");
            var fk = await McpTools.CreateColumn(engine, fact, "CustomerKey", "Int64");
            var pk = await McpTools.CreateColumn(engine, lookup, "CustomerKey", "Int64");
            await McpTools.CreateRelationship(engine, fk, pk, "OneDirection", true);
            var relationship = (await McpTools.GetModelGraph(engine)).Relationships.Single();

            var (result, change) = await CaptureAsync(sessions,
                () => McpTools.SetRelationshipCrossfilter(engine, relationship.Name, "BothDirections"));
            var updatedRelationship = (await McpTools.GetModelGraph(engine)).Relationships.Single();
            AssertAgentCommit(result, change, "relationship:" + updatedRelationship.Name, "CrossFilteringBehavior");
            Assert.Equal("BothDirections", updatedRelationship.CrossFilter);

            Assert.False((await McpTools.SetRelationshipCrossfilter(engine, updatedRelationship.Name, "BothDirections")).Changed);

            await AssertNoCommitAsync(sessions, async () =>
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.SetRelationshipCrossfilter(engine, updatedRelationship.Name, "Sideways"));
                Assert.Contains("single direction", ex.Message);
                Assert.Contains("both directions", ex.Message);
            });
            Assert.Equal("BothDirections", (await McpTools.GetModelGraph(engine)).Relationships.Single().CrossFilter);

            await engine.UndoAsync("human");
            Assert.Equal("OneDirection", (await McpTools.GetModelGraph(engine)).Relationships.Single().CrossFilter);
            await McpTools.RedoChange(engine);
            Assert.Equal("BothDirections", (await McpTools.GetModelGraph(engine)).Relationships.Single().CrossFilter);
        }

        [Fact]
        public async Task Set_partition_m_is_public_broadcast_undoable_and_rejects_empty_M_without_mutation()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.CreateModelAsync("McpPartitionM", 1604);
            const string before = "let Source = 1 in Source";
            const string after = "let Source = 2 in Source";
            var table = await McpTools.CreateImportTable(engine, "Sales", before);
            var partition = (await McpTools.ListPartitions(engine, table)).Single().Ref;

            var (result, change) = await CaptureAsync(sessions, () => McpTools.SetPartitionM(engine, partition, after));
            AssertAgentCommit(result, change, partition, "Expression");
            Assert.Equal(after, await McpTools.GetPartitionM(engine, partition));

            Assert.False((await McpTools.SetPartitionM(engine, partition, after)).Changed);

            await AssertNoCommitAsync(sessions, async () =>
            {
                var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SetPartitionM(engine, partition, "  "));
                Assert.Contains("cannot be empty", ex.Message);
            });
            Assert.Equal(after, await McpTools.GetPartitionM(engine, partition));

            await engine.UndoAsync("human");
            Assert.Equal(before, await McpTools.GetPartitionM(engine, partition));
            await McpTools.RedoChange(engine);
            Assert.Equal(after, await McpTools.GetPartitionM(engine, partition));
        }

        private static ColumnRow Column(IEnumerable<ColumnRow> columns, string objectRef)
            => columns.Single(column => column.Ref == objectRef);

        private static async Task<(SetResult result, ChangeNotification change)> CaptureAsync(
            SessionManager sessions, Func<Task<SetResult>> action)
        {
            var seen = new List<ChangeNotification>();
            void Handler(ChangeNotification notification) => seen.Add(notification);
            sessions.Bus.Changed += Handler;
            try
            {
                var result = await action();
                return (result, Assert.Single(seen));
            }
            finally { sessions.Bus.Changed -= Handler; }
        }

        private static async Task AssertNoCommitAsync(SessionManager sessions, Func<Task> action)
        {
            var revision = sessions.Current.Revision;
            var seen = new List<ChangeNotification>();
            void Handler(ChangeNotification notification) => seen.Add(notification);
            sessions.Bus.Changed += Handler;
            try { await action(); }
            finally { sessions.Bus.Changed -= Handler; }
            Assert.Equal(revision, sessions.Current.Revision);
            Assert.Empty(seen);
        }

        private static void AssertAgentCommit(SetResult result, ChangeNotification change, string objectRef, string property)
        {
            Assert.True(result.Changed);
            Assert.Equal(result.Revision, change.Revision);
            Assert.Equal("agent", change.Origin);
            Assert.Contains(change.Deltas, delta => delta.Ref == objectRef
                && delta.Props != null
                && delta.Props.Contains(property, StringComparer.OrdinalIgnoreCase));
        }
    }
}
