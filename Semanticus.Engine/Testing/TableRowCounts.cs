using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        /// <summary>The named SQL source this table is mapped to, when it is mapped. Null = the coordinates came
        /// from reading the model's own partitions, which is how this check has always worked.</summary>
        public string SqlSourceId { get; set; }
        public string SqlSourceName { get; set; }
        /// <summary>The mapped source's sign-in, carried so the count query signs in the way that source says to
        /// instead of guessing. Null for a detected (unmapped) table, which keeps the old endpoint lookup.</summary>
        public string AuthMode { get; set; }
        public string TenantId { get; set; }
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
        /// <summary>The named SQL source behind this row, when the table is mapped to one. Null = detected from
        /// the model, so a surface can say "mapped to Contoso warehouse" or offer to map it.</summary>
        public string SqlSourceId { get; set; }
        public string SqlSourceName { get; set; }
        public long? ModelCount { get; set; }
        public long? SourceCount { get; set; }
        public string ModelObservedUtc { get; set; }
        public string SourceObservedUtc { get; set; }
        public bool SnapshotAligned { get; set; }
        public CheckResult Check { get; set; }
    }

    /// <summary>
    /// One model table, pointed at one named SQL source and one table inside it. This is the missing action Astra
    /// found: the row-count check could only ever use what the model's partitions happened to reveal, so a table it
    /// could not read stayed unreadable with no way to repair it.
    ///
    /// It lives with the model's other test data (<c>.semanticus/tests/</c>) rather than in the machine registry,
    /// because which table in a warehouse a model table came from is a fact ABOUT THIS MODEL and should travel with
    /// it. The SOURCE it names is machine-local, which is why only its id is stored here.
    /// </summary>
    public sealed class TableSourceMapping
    {
        public string ModelTable { get; set; }
        public string ModelIdentity { get; set; }   // durable pane identity; a shared sidecar is filtered by this
        public string SqlSourceId { get; set; }
        public string Schema { get; set; }
        public string Entity { get; set; }
        public string SetWhenUtc { get; set; }
        public string SetBy { get; set; }           // "human" | "agent"
    }

    /// <summary>
    /// Persistence for the table mappings, cloning the <see cref="TestSuiteStore"/> discipline exactly: plain JSONL
    /// beside the suite, best-effort, unreadable lines counted rather than thrown, atomic temp-then-move so a crash
    /// mid-write can never tear the file. Keyed by model table name within one model identity.
    /// </summary>
    public static class TableSourceMappingStore
    {
        public const string FileName = "table-sources.jsonl";

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Every mapping in the sidecar. The caller filters to the open model's identity.</summary>
        public static (List<TableSourceMapping> Mappings, int Unreadable) Load(string dir)
        {
            lock (Gate) return LoadUnlocked(dir);
        }

        /// <summary>Set (or replace) the mapping for one table. Returns the stored mapping, or null on failure —
        /// never throws, like the suite store it sits beside.</summary>
        public static TableSourceMapping Upsert(string dir, TableSourceMapping mapping)
        {
            if (string.IsNullOrEmpty(dir) || mapping == null || string.IsNullOrWhiteSpace(mapping.ModelTable)) return null;
            try
            {
                lock (Gate)
                {
                    var (all, _) = LoadUnlocked(dir);
                    all.RemoveAll(x => Same(x, mapping.ModelTable, mapping.ModelIdentity));
                    all.Add(mapping);
                    WriteUnlocked(dir, all);
                }
                return mapping;
            }
            catch { return null; }
        }

        /// <summary>Remove one table's mapping. False when there was nothing to remove.</summary>
        public static bool Remove(string dir, string modelTable, string modelIdentity)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrWhiteSpace(modelTable)) return false;
            try
            {
                lock (Gate)
                {
                    var (all, _) = LoadUnlocked(dir);
                    if (all.RemoveAll(x => Same(x, modelTable, modelIdentity)) == 0) return false;
                    WriteUnlocked(dir, all);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>How many table mappings point at one named source. The number a refused delete says out loud.</summary>
        public static int CountUsing(IEnumerable<TableSourceMapping> mappings, string sqlSourceId) =>
            mappings == null || string.IsNullOrWhiteSpace(sqlSourceId) ? 0
                : mappings.Count(m => string.Equals(m?.SqlSourceId, sqlSourceId, StringComparison.OrdinalIgnoreCase));

        private static bool Same(TableSourceMapping x, string modelTable, string modelIdentity) =>
            string.Equals(x?.ModelTable, modelTable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x?.ModelIdentity ?? "", modelIdentity ?? "", StringComparison.Ordinal);

        private static (List<TableSourceMapping> Mappings, int Unreadable) LoadUnlocked(string dir)
        {
            var all = new List<TableSourceMapping>();
            var bad = 0;
            try
            {
                var file = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, TestSuiteStore.SubDir, FileName);
                if (file == null || !File.Exists(file)) return (all, 0);
                foreach (var line in File.ReadAllLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    TableSourceMapping m = null;
                    try { m = JsonSerializer.Deserialize<TableSourceMapping>(line, JsonOpts); } catch { }
                    if (m != null && !string.IsNullOrWhiteSpace(m.ModelTable)) all.Add(m); else bad++;
                }
            }
            catch { /* unreadable store means no mappings, never a throw */ }
            return (all, bad);
        }

        private static void WriteUnlocked(string dir, List<TableSourceMapping> all)
        {
            var file = Path.Combine(dir, TestSuiteStore.SubDir, FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var body = string.Join("\n", all.Select(m => JsonSerializer.Serialize(m, JsonOpts)));
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, all.Count == 0 ? "" : body + "\n");
            File.Move(tmp, file, overwrite: true);
        }
    }

    /// <summary>Pure discovery and judgement for ambient model-vs-source table row counts.</summary>
    public static class TableRowCountReconciliation
    {
        public static IReadOnlyList<TableRowCountInput> Discover(Model model) => Discover(model, null);

        /// <summary>
        /// Where each table's row count will be read from. A saved mapping WINS: the person said which SQL source and
        /// which table in it, and that beats anything guessed from a partition. With no mapping this is exactly the
        /// old behaviour, reading the model's own partitions. With neither, the row says so in plain words instead of
        /// disappearing, so a surface has something to offer a Map source action against.
        /// </summary>
        public static IReadOnlyList<TableRowCountInput> Discover(Model model, IReadOnlyList<TableSourceMapping> mappings)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var sources = SqlSourceDiscovery.Find(model);
            var detected = sources.GroupBy(s => s.ModelTable, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var mapped = (mappings ?? Array.Empty<TableSourceMapping>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.ModelTable))
                .GroupBy(m => m.ModelTable, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            var tables = detected.Keys.Concat(mapped.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
            var results = new List<TableRowCountInput>();
            foreach (var table in tables)
            {
                detected.TryGetValue(table, out var group);
                if (mapped.TryGetValue(table, out var mapping))
                {
                    results.Add(FromMapping(table, mapping, group));
                    continue;
                }
                results.Add(FromDetection(table, group));
            }
            return results.OrderBy(r => r.ModelTable, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        // A mapped table reads its endpoint and its sign-in from the NAMED source, and its schema/entity from the
        // mapping — falling back to what the model detected only where the mapping left a blank.
        private static TableRowCountInput FromMapping(string table, TableSourceMapping mapping, List<ReconcileSourceCandidate> detected)
        {
            var fallback = detected?.FirstOrDefault(IsComplete) ?? detected?.FirstOrDefault();
            var schema = Pick(mapping.Schema, fallback?.Schema);
            var entity = Pick(mapping.Entity, fallback?.Entity);
            var resolved = SqlSources.Resolve(mapping.SqlSourceId, null, null, null, null);
            if (resolved.Error != null)
                return new TableRowCountInput
                {
                    ModelTable = table, Schema = schema, Entity = entity, SqlSourceId = mapping.SqlSourceId,
                    DiscoveryError = "this table is mapped to a SQL source that is no longer saved. Map it to a source you still have",
                };
            if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(entity))
                return new TableRowCountInput
                {
                    ModelTable = table, Server = resolved.Server, Database = resolved.Database,
                    Schema = schema, Entity = entity, SqlSourceId = resolved.SqlSourceId, SqlSourceName = resolved.SourceName,
                    DiscoveryError = "this table's mapping does not say which schema and table in the source to count. Finish the mapping",
                };
            return new TableRowCountInput
            {
                ModelTable = table, Server = resolved.Server, Database = resolved.Database,
                Schema = schema, Entity = entity,
                SqlSourceId = resolved.SqlSourceId, SqlSourceName = resolved.SourceName,
                AuthMode = resolved.AuthMode, TenantId = resolved.TenantId,
            };
        }

        // No mapping: exactly the old reading of the model's partitions.
        private static TableRowCountInput FromDetection(string table, List<ReconcileSourceCandidate> group)
        {
            var complete = (group ?? new List<ReconcileSourceCandidate>()).Where(IsComplete)
                .GroupBy(SourceKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
            if (complete.Count == 1)
            {
                var source = complete[0];
                return new TableRowCountInput
                {
                    ModelTable = source.ModelTable, Server = source.Server, Database = source.Database,
                    Schema = source.Schema, Entity = source.Entity,
                };
            }
            var first = group?.FirstOrDefault();
            return new TableRowCountInput
            {
                ModelTable = table, Server = first?.Server, Database = first?.Database,
                Schema = first?.Schema, Entity = first?.Entity,
                DiscoveryError = complete.Count == 0
                    ? "this table is not mapped to a SQL source yet. Map it to a SQL source, a schema and a table name"
                    : $"the table has {complete.Count} distinct physical SQL sources, so no source was guessed",
            };
        }

        private static string Pick(string first, string second) =>
            !string.IsNullOrWhiteSpace(first) ? first.Trim() : (string.IsNullOrWhiteSpace(second) ? null : second.Trim());

        public static TableRowCountResult Evaluate(TableRowCountInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var result = new TableRowCountResult
            {
                ModelTable = input.ModelTable, Server = input.Server, Database = input.Database,
                Schema = input.Schema, Entity = input.Entity,
                SqlSourceId = input.SqlSourceId, SqlSourceName = input.SqlSourceName,
                ModelCount = NonNegative(input.ModelCount),
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
                // Timing is ONE reason the two numbers can differ; saying only that invites the reader to
                // conclude the data is fine. Say what was and was not established instead.
                result.Check = Nv($"Counts differ (model {input.ModelCount:N0}, source {input.SourceCount:N0}). "
                    + "Matching snapshots were not confirmed, so this alone does not prove missing rows.");
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
