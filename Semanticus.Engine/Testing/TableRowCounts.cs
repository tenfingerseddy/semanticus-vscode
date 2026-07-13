using System;
using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public static class TableRowCountChecks
    {
        public const string RowCount = "TableRowCount";
    }

    /// <summary>One physical SQL-backed model table and the two independently observed row counts.</summary>
    public sealed class TableRowCountInput
    {
        public string ModelTable { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Entity { get; set; }
        public long? ModelCount { get; set; }
        public long? SourceCount { get; set; }
        public string ModelObservedUtc { get; set; }
        public string SourceObservedUtc { get; set; }
        public string ModelError { get; set; }
        public string SourceError { get; set; }
        public string DiscoveryError { get; set; }
        public bool SnapshotAligned { get; set; }
    }

    public sealed class TableRowCountResult
    {
        public string ModelTable { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Entity { get; set; }
        public long? ModelCount { get; set; }
        public long? SourceCount { get; set; }
        public string ModelObservedUtc { get; set; }
        public string SourceObservedUtc { get; set; }
        public bool SnapshotAligned { get; set; }
        public CheckResult Check { get; set; }
    }

    /// <summary>Pure discovery and judgement for ambient model-vs-source table row counts.</summary>
    public static class TableRowCountReconciliation
    {
        public static IReadOnlyList<TableRowCountInput> Discover(Model model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var sources = SqlSourceDiscovery.Find(model);
            var results = new List<TableRowCountInput>();
            foreach (var group in sources.GroupBy(s => s.ModelTable, StringComparer.OrdinalIgnoreCase))
            {
                var complete = group.Where(IsComplete)
                    .GroupBy(SourceKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()).ToList();
                if (complete.Count == 1)
                {
                    var source = complete[0];
                    results.Add(new TableRowCountInput
                    {
                        ModelTable = source.ModelTable, Server = source.Server, Database = source.Database,
                        Schema = source.Schema, Entity = source.Entity,
                    });
                }
                else
                {
                    var first = group.First();
                    results.Add(new TableRowCountInput
                    {
                        ModelTable = first.ModelTable, Server = first.Server, Database = first.Database,
                        Schema = first.Schema, Entity = first.Entity,
                        DiscoveryError = complete.Count == 0
                            ? "the SQL source mapping is incomplete (endpoint, database, schema, or entity is missing)"
                            : $"the table has {complete.Count} distinct physical SQL sources, so no source was guessed",
                    });
                }
            }
            return results.OrderBy(r => r.ModelTable, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static TableRowCountResult Evaluate(TableRowCountInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var result = new TableRowCountResult
            {
                ModelTable = input.ModelTable, Server = input.Server, Database = input.Database,
                Schema = input.Schema, Entity = input.Entity, ModelCount = NonNegative(input.ModelCount),
                SourceCount = NonNegative(input.SourceCount), ModelObservedUtc = input.ModelObservedUtc,
                SourceObservedUtc = input.SourceObservedUtc, SnapshotAligned = input.SnapshotAligned,
            };
            if (!string.IsNullOrWhiteSpace(input.DiscoveryError))
                result.Check = Nv(input.DiscoveryError);
            else if (!string.IsNullOrWhiteSpace(input.ModelError))
                result.Check = Nv("model COUNTROWS could not be read: " + input.ModelError);
            else if (!string.IsNullOrWhiteSpace(input.SourceError))
                result.Check = Nv("source COUNT_BIG could not be read: " + input.SourceError);
            else if (!input.ModelCount.HasValue || !input.SourceCount.HasValue)
                result.Check = Nv("both model COUNTROWS and source COUNT_BIG must be observed");
            else if (input.ModelCount < 0 || input.SourceCount < 0)
                result.Check = Nv("a row-count observation was negative and therefore invalid");
            else if (input.ModelCount == input.SourceCount)
                result.Check = new CheckResult
                {
                    Check = TableRowCountChecks.RowCount, Verdict = Verdict.Pass, Count = input.ModelCount,
                    Message = $"model and source both returned {input.ModelCount:N0} rows",
                };
            // SnapshotAligned is set NOWHERE in production today; every live mismatch routes to the NV
            // branch below. Fail feeds the D-cap hard gate, so this flag must only ever be set on genuine
            // snapshot-isolation proof (never timestamp proximity) or ingestion timing becomes a false Fail.
            else if (input.SnapshotAligned)
                result.Check = new CheckResult
                {
                    Check = TableRowCountChecks.RowCount, Verdict = Verdict.Fail,
                    Count = Math.Abs(input.ModelCount.Value - input.SourceCount.Value),
                    Message = $"snapshot-aligned counts differ: model {input.ModelCount:N0}, source {input.SourceCount:N0}",
                };
            else
                result.Check = Nv($"counts differ (model {input.ModelCount:N0}, source {input.SourceCount:N0}), but the independent connections may have observed different snapshots");
            return result;
        }

        private static bool IsComplete(ReconcileSourceCandidate s) =>
            !string.IsNullOrWhiteSpace(s.Server) && !string.IsNullOrWhiteSpace(s.Database)
            && !string.IsNullOrWhiteSpace(s.Schema) && !string.IsNullOrWhiteSpace(s.Entity);
        private static string SourceKey(ReconcileSourceCandidate s) =>
            string.Join("\u001f", s.Server.Trim(), s.Database.Trim(), s.Schema.Trim(), s.Entity.Trim());
        private static long? NonNegative(long? value) => value >= 0 ? value : null;
        private static CheckResult Nv(string message) => new CheckResult
        {
            Check = TableRowCountChecks.RowCount, Verdict = Verdict.NotVerifiable, Message = message,
        };
    }

    /// <summary>One source detector shared by mapping review and ambient integrity checks.</summary>
    internal static class SqlSourceDiscovery
    {
        internal static IReadOnlyList<ReconcileSourceCandidate> Find(Model model)
        {
            var candidates = new List<ReconcileSourceCandidate>();
            foreach (var table in model.Tables)
            foreach (var partition in table.Partitions)
            {
                string server = null, database = null, schema = null, entity = null;
                if (partition is EntityPartition ep)
                {
                    schema = ep.SchemaName; entity = ep.EntityName;
                    if (!string.IsNullOrWhiteSpace(ep.ExpressionSource?.Expression))
                        SchemaSync.TryParseSqlSource(ep.ExpressionSource.Expression, out server, out database, out _, out _);
                }
                else if (partition.SourceType == PartitionSourceType.M && !string.IsNullOrWhiteSpace(partition.Expression))
                    SchemaSync.TryParseSqlSource(partition.Expression, out server, out database, out schema, out entity);
                var source = partition.StructuredDataSource;
                if (source != null)
                {
                    if (string.IsNullOrWhiteSpace(server)) server = source.Server;
                    if (string.IsNullOrWhiteSpace(database)) database = source.Database;
                }
                if (new[] { server, database, schema, entity }.All(string.IsNullOrWhiteSpace)) continue;
                candidates.Add(new ReconcileSourceCandidate
                {
                    ModelTable = table.Name, Server = server, Database = database, Schema = schema, Entity = entity,
                });
            }
            return candidates.GroupBy(c => string.Join("\u001f", c.ModelTable, c.Server, c.Database, c.Schema, c.Entity), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToArray();
        }
    }
}
