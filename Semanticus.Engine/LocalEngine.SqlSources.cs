using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>What a refused delete says out loud: not just "no", but how many things still point at the source.</summary>
    public sealed class SqlSourceDeleteResult
    {
        public bool Deleted { get; set; }
        public string Note { get; set; }
        public int ChecksUsing { get; set; }
        public int TableMappingsUsing { get; set; }
        /// <summary>The titles of the saved checks still pointing at this source, so a surface can say WHAT needs
        /// repair instead of only how much. Sorted, so the list reads the same every time.</summary>
        public string[] CheckTitles { get; set; } = Array.Empty<string>();
        /// <summary>The model tables whose mapping still points at this source. Sorted for the same reason.</summary>
        public string[] MappedTables { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// One model table as the Table row counts page shows it. This row IS the check: there is no saved-test kind for
    /// a table count, and "New check, Table row count" and "Map source" are the same action on this record
    /// (Astra's point 5). Every table gets a row, including one with no source at all, so the page always has
    /// something to offer a Map source action against.
    /// </summary>
    public sealed class TableSourceMappingRow
    {
        public string ModelTable { get; set; }
        /// <summary>"mapping" (the person chose it), "detected" (read from the table's own partition), or "none".</summary>
        public string Source { get; set; }
        public string SqlSourceId { get; set; }
        public string SqlSourceName { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Entity { get; set; }
        /// <summary>True when this table has everything it needs to be counted. False means <see cref="Reason"/>
        /// says what is missing.</summary>
        public bool CanCount { get; set; }
        /// <summary>Plain words for why it cannot be counted yet, or null.</summary>
        public string Reason { get; set; }
        /// <summary>Always true: every row can be pointed at a source through set_table_source_mapping, including
        /// the ones whose detected source is missing or ambiguous.</summary>
        public bool Editable { get; set; } = true;
        /// <summary>True when a mapping the person made is overriding what the model itself says.</summary>
        public bool OverridesDetected { get; set; }
        public string SetWhenUtc { get; set; }

        // ---- the latest count outcome, from the last run in this session. Null = it has not been counted yet,
        // which is its own state and never reported as a pass or a failure.
        public string LastVerdict { get; set; }
        public string LastMessage { get; set; }
        public long? LastModelCount { get; set; }
        public long? LastSourceCount { get; set; }
        public string LastRunUtc { get; set; }
    }

    /// <summary>Every table, where its row count is read from, and how the last count went.</summary>
    public sealed class TableSourceMappingList
    {
        public TableSourceMappingRow[] Rows { get; set; } = Array.Empty<TableSourceMappingRow>();
        /// <summary>Plain-words context, for example that nothing has been counted yet this session.</summary>
        public string Note { get; set; }
    }

    /// <summary>One table's mapping as both doors see it, with the source named rather than only its id.</summary>
    public sealed class TableSourceMappingInfo
    {
        public string ModelTable { get; set; }
        public string SqlSourceId { get; set; }
        public string SqlSourceName { get; set; }
        public string Schema { get; set; }
        public string Entity { get; set; }
        public string SetWhenUtc { get; set; }
        public string Note { get; set; }
    }

    /// <summary>What one saved SQL source is used by, and nothing else. Tests and Saved reports are a Pro feature,
    /// so the two listings Connections used to call are Pro: list_tests returns whole check definitions and
    /// list_table_mappings returns saved verdicts and row counts. This is the shared FREE projection that keeps
    /// Connections working: the source, and the names of what points at it. No definitions, no expected values, no
    /// verdicts, no row counts. Adding a field here is adding a hole in the Tests gate.</summary>
    public sealed class SqlSourceUsage
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>How many saved checks point at this source.</summary>
        public int ChecksUsing { get; set; }
        /// <summary>How many model tables are mapped to this source.</summary>
        public int TableMappingsUsing { get; set; }
        /// <summary>The titles of those checks, so a surface can say WHAT would break, not only how much. Sorted.</summary>
        public string[] CheckTitles { get; set; } = Array.Empty<string>();
        /// <summary>The model tables mapped to this source. Table names only, sorted.</summary>
        public string[] MappedTables { get; set; } = Array.Empty<string>();
    }

    // ============================================================================================
    // Named SQL sources, and the table mappings that use them (Kane's Tests redesign, 2026-09-15).
    //
    // The sources are MACHINE-LOCAL, like the model connections beside them: an endpoint and a way to
    // sign in belong to this device. The table mappings are per MODEL, stored with the model's other
    // test data, because which table in a warehouse a model table came from is a fact about the model
    // and should travel with it.
    //
    // Both doors land here. Nothing in this file holds a credential: a source carries a sign-in mode
    // NAME, and the token is acquired at use time through the same Entra helper the XMLA side uses.
    // ============================================================================================
    public sealed partial class LocalEngine
    {
        /// <summary>
        /// Every table in the model, where its row count is read from, and how the last count went. This is the ONE
        /// home for a table-count check (Astra's point 5): the row IS the check, "New check, Table row count" and
        /// "Map source" both land on <see cref="SetTableSourceMappingAsync"/>, and there is deliberately no saved-test
        /// kind for a table count that could drift away from it.
        ///
        /// Unlike the ambient run, this lists EVERY table, including one with no SQL source at all, so the page
        /// always has a row to offer a Map source action against. The run itself still only counts what it can, so
        /// adding a table here never dilutes a run's coverage.
        /// </summary>
        public async Task<TableSourceMappingList> ListTableSourceMappingsAsync()
        {
            RequireProFeature();
            var s = _sessions.Current;
            if (s == null) return new TableSourceMappingList { Note = "No open model." };

            var mappings = await ScopedTableMappingsAsync(s);
            var byTable = mappings.Where(m => !string.IsNullOrWhiteSpace(m.ModelTable))
                .GroupBy(m => m.ModelTable, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            // ONE model read: the table names plus what the ambient discovery would make of each of them.
            var (names, calculated, discovered) = await s.ReadAsync(m => (
                m.Tables.Select(t => t.Name).ToArray(),
                m.Tables.Where(t => t is CalculatedTable || t is CalculationGroupTable)
                    .Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                TableRowCountReconciliation.Discover(m, mappings)
                    .ToDictionary(d => d.ModelTable, d => d, StringComparer.OrdinalIgnoreCase)));

            // The last counts this session actually TOOK, for THIS model. Reading them off the last run meant a
            // run that did not count tables wiped them, so a person who reran one saved check was told nothing
            // had ever been counted (Astra, 2026-09-15). Counts from another model are never shown here: they
            // would put one model's numbers under another model's table names.
            var openIdentity = PaneIdentity(s, _live);
            var retained = openIdentity != null
                && string.Equals(_lastTableCounts.ModelIdentity, openIdentity, StringComparison.Ordinal)
                    ? _lastTableCounts
                    : default;
            var lastCounts = (retained.Results ?? Array.Empty<TableRowCountResult>())
                .GroupBy(r => r.ModelTable, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<TableSourceMappingRow>();
            foreach (var name in names)
            {
                byTable.TryGetValue(name, out var mapping);
                discovered.TryGetValue(name, out var found);

                var row = new TableSourceMappingRow
                {
                    ModelTable = name,
                    Source = mapping != null ? "mapping" : (found != null && string.IsNullOrEmpty(found.DiscoveryError) ? "detected" : "none"),
                    SqlSourceId = found?.SqlSourceId ?? mapping?.SqlSourceId,
                    SqlSourceName = found?.SqlSourceName,
                    Server = found?.Server,
                    Database = found?.Database,
                    Schema = found?.Schema ?? mapping?.Schema,
                    Entity = found?.Entity ?? mapping?.Entity,
                    OverridesDetected = mapping != null,
                    SetWhenUtc = mapping?.SetWhenUtc,
                };

                if (calculated.Contains(name))
                {
                    row.Source = "none";
                    row.CanCount = false;
                    row.Reason = "this table is worked out inside the model, so there is no source table to count it against";
                    row.Editable = false;
                }
                else if (found == null)
                {
                    row.CanCount = false;
                    row.Reason = "this table is not mapped to a SQL source yet. Map it to a SQL source, a schema and a table name";
                }
                else if (!string.IsNullOrEmpty(found.DiscoveryError))
                {
                    row.CanCount = false;
                    row.Reason = found.DiscoveryError;
                }
                else row.CanCount = true;

                if (lastCounts.TryGetValue(name, out var last))
                {
                    row.LastVerdict = last.Check?.Verdict.ToString();
                    row.LastMessage = last.Check?.Message;
                    row.LastModelCount = last.ModelCount;
                    row.LastSourceCount = last.SourceCount;
                    row.LastRunUtc = last.SourceObservedUtc ?? last.ModelObservedUtc ?? retained.When;
                }

                rows.Add(row);
            }

            return new TableSourceMappingList
            {
                Rows = rows.OrderBy(r => r.ModelTable, StringComparer.OrdinalIgnoreCase).ToArray(),
                Note = retained.Results == null ? "No table has been counted yet. Run the checks to fill in the last result." : null,
            };
        }

        public Task<SqlSourceRecord[]> ListSqlSourcesAsync()
            => Task.FromResult(SqlSourceRegistry.List().ToArray());

        /// <summary>Which saved checks and which table mappings use each saved SQL source. Free on both doors, and
        /// deliberately the smallest answer that keeps Connections honest: it names what would break if a source
        /// were removed or re-pointed, without handing the free tier any of the Pro Tests data.
        ///
        /// Same honest limit as delete_sql_source: it can only see the model that is open. With no model open every
        /// source comes back with no usage rather than an error, because Connections renders before a model opens.</summary>
        public async Task<SqlSourceUsage[]> ListSqlSourceUsageAsync()
        {
            var sources = SqlSourceRegistry.List().ToArray();
            if (sources.Length == 0) return Array.Empty<SqlSourceUsage>();

            var s = _sessions.Current;
            if (s == null)
                return sources.Select(r => new SqlSourceUsage { Id = r.Id, Name = r.Name }).ToArray();

            var defs = (await LoadScopedTestDefinitionsAsync(s)).Defs;
            var mappings = await ScopedTableMappingsAsync(s);
            return sources.Select(r =>
            {
                var titles = SqlSources.TitlesOfChecksUsing(defs, r.Id);
                var tables = mappings
                    .Where(m => string.Equals(m.SqlSourceId, r.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.ModelTable)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new SqlSourceUsage
                {
                    Id = r.Id,
                    Name = r.Name,
                    ChecksUsing = titles.Length,
                    TableMappingsUsing = tables.Length,
                    CheckTitles = titles,
                    MappedTables = tables,
                };
            }).ToArray();
        }

        /// <summary>Create a named SQL source, or update one by id. The registry refuses a connection string, an
        /// unknown sign-in mode and a duplicate name, all in plain words.</summary>
        public Task<SqlSourceRecord> SaveSqlSourceAsync(string id, string name, string server, string database,
            string authMode, string tenantId, string origin = "human")
            => Task.FromResult(SqlSourceRegistry.Save(id, name, server, database, authMode, tenantId));

        /// <summary>
        /// Remove a named SQL source, REFUSED while the open model's checks or table mappings still point at it, with
        /// the counts said out loud.
        ///
        /// Honest limit: this can only see the model that is open. A source referenced by a model that is NOT open can
        /// still be removed, and those checks then report "the SQL source this was set to use is no longer saved"
        /// rather than running somewhere else. That is the safe failure, not a silent one.
        /// </summary>
        public async Task<SqlSourceDeleteResult> DeleteSqlSourceAsync(string id, string origin = "human")
        {
            var rec = SqlSourceRegistry.Find(id);
            if (rec == null) return new SqlSourceDeleteResult { Deleted = false, Note = "There is no saved SQL source with that id." };

            var s = _sessions.Current;
            if (s == null)
                return new SqlSourceDeleteResult
                {
                    Deleted = false,
                    Note = $"Open the model first. Until it is open, Semanticus cannot see which checks use '{rec.Name}', and removing it would break them silently.",
                };

            var loaded = await LoadScopedTestDefinitionsAsync(s);
            // NAME what needs repair, not just how much of it there is (Astra's point 4): a person told "2 checks"
            // has to go hunting, and a person told which two can go and fix them.
            var checkTitles = SqlSources.TitlesOfChecksUsing(loaded.Defs, rec.Id);
            var mappedTables = (await ScopedTableMappingsAsync(s))
                .Where(m => string.Equals(m.SqlSourceId, rec.Id, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.ModelTable)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (checkTitles.Length > 0 || mappedTables.Length > 0)
                return new SqlSourceDeleteResult
                {
                    Deleted = false,
                    ChecksUsing = checkTitles.Length,
                    TableMappingsUsing = mappedTables.Length,
                    CheckTitles = checkTitles,
                    MappedTables = mappedTables,
                    Note = $"'{rec.Name}' is still in use, so it was not removed. {Uses(checkTitles, mappedTables)} Point those at another source, or remove them, then remove this source.",
                };

            return new SqlSourceDeleteResult
            {
                Deleted = SqlSourceRegistry.Delete(rec.Id),
                Note = $"'{rec.Name}' was removed. Nothing was using it.",
            };
        }

        /// <summary>
        /// Open a connection with this source's own sign-in and run one test query. Agent origin goes through the
        /// standard QueryData gate on the SQL target, exactly like preview_table or the reconciliation run, and can
        /// never trigger an interactive sign-in.
        /// </summary>
        public async Task<SqlSourceTestResult> TestSqlSourceAsync(string id, string origin = "human")
        {
            var rec = SqlSourceRegistry.Find(id)
                ?? throw new InvalidOperationException("There is no saved SQL source with that id. Open Connections to see the saved sources.");

            var gate = GuardAgent(AgentCapability.QueryData, rec.Server, rec.Database, origin, isCommit: true,
                summary: $"test the SQL source '{rec.Name}': open {rec.Database} on {rec.Server} and run one test query",
                intentBasis: "querydata", consumeGrant: false);
            if (gate != null)
                return new SqlSourceTestResult
                {
                    Id = rec.Id, Name = rec.Name, Server = rec.Server, Database = rec.Database,
                    Ok = false, Note = gate,
                };

            return await SqlSourceProbe.TestAsync(rec, DeployGuard.IsAgent(origin), CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>Point one model table at a named SQL source and the table inside it. Replaces any mapping that
        /// table already had.</summary>
        public async Task<TableSourceMappingInfo> SetTableSourceMappingAsync(string table, string sqlSourceId,
            string schema, string entity, string origin = "human")
        {
            RequireProFeature();
            var s = _sessions.Current ?? throw new InvalidOperationException("No open model. Use open_model, connect_local or connect_xmla first.");
            if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("Name the model table to map.", nameof(table));
            var source = SqlSourceRegistry.Find(sqlSourceId)
                ?? throw new InvalidOperationException("There is no saved SQL source with that id. Save the source in Connections first, then map the table to it.");

            var dir = TestsDirFor(s) ?? throw new InvalidOperationException(
                "This session has no on-disk home. Save the model (save_model) so the .semanticus sidecar exists, then retry.");
            var identity = PaneIdentity(s, null) ?? throw new InvalidOperationException(
                "This unsaved model has no durable identity. Save it (save_model) or open the model before mapping a table.");

            var resolvedName = await s.ReadAsync(m => (ObjectRefs.Resolve(m, table) as Table)?.Name);
            if (resolvedName == null)
                throw new ArgumentException($"'{table}' is not a table in this model. Run list_objects to see the model's tables.", nameof(table));

            var mapping = TableSourceMappingStore.Upsert(dir, new TableSourceMapping
            {
                ModelTable = resolvedName,
                ModelIdentity = identity,
                SqlSourceId = source.Id,
                Schema = string.IsNullOrWhiteSpace(schema) ? null : schema.Trim(),
                Entity = string.IsNullOrWhiteSpace(entity) ? null : entity.Trim(),
                SetWhenUtc = DateTime.UtcNow.ToString("o"),
                SetBy = origin,
            }) ?? throw new InvalidOperationException("The mapping could not be written to .semanticus/tests/table-sources.jsonl.");

            var incomplete = string.IsNullOrWhiteSpace(mapping.Schema) || string.IsNullOrWhiteSpace(mapping.Entity);
            return new TableSourceMappingInfo
            {
                ModelTable = mapping.ModelTable, SqlSourceId = source.Id, SqlSourceName = source.Name,
                Schema = mapping.Schema, Entity = mapping.Entity, SetWhenUtc = mapping.SetWhenUtc,
                Note = incomplete
                    ? $"'{mapping.ModelTable}' is mapped to '{source.Name}'. Add the schema and the table name in the source so the row count can be read."
                    : $"'{mapping.ModelTable}' is mapped to {mapping.Schema}.{mapping.Entity} in '{source.Name}'.",
            };
        }

        /// <summary>Forget one table's mapping. The row count then falls back to reading the model's own partitions,
        /// which is where it was before anyone mapped anything. False when there was no mapping.</summary>
        public async Task<bool> ClearTableSourceMappingAsync(string table, string origin = "human")
        {
            RequireProFeature();
            var s = _sessions.Current ?? throw new InvalidOperationException("No open model. Use open_model, connect_local or connect_xmla first.");
            if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("Name the model table to unmap.", nameof(table));
            var dir = TestsDirFor(s);
            if (dir == null) return false;
            var identity = PaneIdentity(s, null);
            var resolvedName = await s.ReadAsync(m => (ObjectRefs.Resolve(m, table) as Table)?.Name) ?? table.Trim();
            return TableSourceMappingStore.Remove(dir, resolvedName, identity);
        }

        /// <summary>The open model's table mappings. A sidecar can be shared by more than one model, so a mapping is
        /// claimed only when its recorded model identity is this one — the same rule the saved checks follow.</summary>
        internal Task<List<TableSourceMapping>> ScopedTableMappingsAsync(Session s)
        {
            var dir = TestsDirFor(s);
            if (dir == null) return Task.FromResult(new List<TableSourceMapping>());
            var identity = PaneIdentity(s, null);
            var (all, _) = TableSourceMappingStore.Load(dir);
            if (string.IsNullOrEmpty(identity)) return Task.FromResult(new List<TableSourceMapping>());
            return Task.FromResult(all.Where(m => string.Equals(m.ModelIdentity, identity, StringComparison.Ordinal)).ToList());
        }

        private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

        // "2 checks use it (revenue vs source, units vs source), and 1 table is mapped to it (Sales)." The names
        // come first, because the name is the part a person can act on.
        private static string Uses(string[] checkTitles, string[] mappedTables)
        {
            var parts = new List<string>();
            if (checkTitles.Length > 0)
                parts.Add($"{Count(checkTitles.Length, "check")} {(checkTitles.Length == 1 ? "uses" : "use")} it ({string.Join(", ", checkTitles)})");
            if (mappedTables.Length > 0)
                parts.Add($"{Count(mappedTables.Length, "table")} {(mappedTables.Length == 1 ? "is" : "are")} mapped to it ({string.Join(", ", mappedTables)})");
            return string.Join(", and ", parts) + ".";
        }
    }
}
