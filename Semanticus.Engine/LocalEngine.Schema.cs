using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// Update Schema (Tabular-Editor-style): re-read a table's SOURCE columns schema-only, diff them against the
    /// model's current columns, and apply a chosen subset as one undoable change. The source-read reuses the
    /// engine's existing Fabric/SQL introspection (<see cref="FabricSqlSchema"/> over TDS with an Entra token) —
    /// the server/database/schema/table are parsed from the partition's M (or read off a Direct-Lake entity
    /// partition). It NEVER throws on an unreachable source: an offline snapshot, a non-SQL source, a native-query
    /// partition, or an auth/connect failure all degrade to Reachable=false + a clear message. Diff + apply are
    /// source-agnostic and fully offline-testable (supply the source columns, or accept the live probe).
    /// </summary>
    public sealed partial class LocalEngine
    {
        public async Task<SourceSchema> GetSourceSchemaAsync(string tableRef, string authMode, string tenantId)
        {
            var s = _sessions.Require();

            // Resolve the table + derive its SQL source coordinates on the model thread (a pure read — no mutation).
            var probe = await s.ReadAsync(m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t))
                    return new SourceSchema { TableRef = tableRef, Reachable = false, Error = $"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables." };

                var res = new SourceSchema { Table = t.Name, TableRef = ObjectRefs.For(t) };
                string server = null, database = null, schema = null, item = null;
                foreach (var p in t.Partitions)
                {
                    if (p is EntityPartition ep)
                    {
                        // Direct Lake / Entity: the physical table + schema live on the partition; the server/db are
                        // in the shared M expression it reads (create_directlake_table via create_named_expression),
                        // or on a structured data source (create_data_source).
                        item = ep.EntityName; schema = ep.SchemaName;
                        var m2 = ep.ExpressionSource?.Expression;
                        if (!string.IsNullOrWhiteSpace(m2)) SchemaSync.TryParseSqlSource(m2, out server, out database, out _, out _);
                    }
                    else if (p.SourceType == PartitionSourceType.M && !string.IsNullOrWhiteSpace(p.Expression))
                    {
                        SchemaSync.TryParseSqlSource(p.Expression, out server, out database, out schema, out item);
                    }

                    // A structured data source (Server/Database properties) fills gaps the M didn't.
                    var sds = p.StructuredDataSource;
                    if (sds != null)
                    {
                        if (string.IsNullOrWhiteSpace(server)) server = sds.Server;
                        if (string.IsNullOrWhiteSpace(database)) database = sds.Database;
                    }

                    if (!string.IsNullOrWhiteSpace(item) && !string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(database)) break;
                }
                res.Server = server; res.Database = database; res.SchemaName = schema; res.Entity = item;
                return res;
            });

            if (!string.IsNullOrEmpty(probe.Error)) return probe;   // not a table — surface as-is

            if (string.IsNullOrWhiteSpace(probe.Server) || string.IsNullOrWhiteSpace(probe.Database) || string.IsNullOrWhiteSpace(probe.Entity))
            {
                probe.Reachable = false;
                probe.Error = "source unreachable — can't refresh schema: this table has no SQL/Fabric source query the engine can probe " +
                    "(an offline snapshot, a non-SQL source, or a native-query partition). Update Schema needs a Sql.Database(...) table-navigation source.";
                return probe;
            }

            // Read the source schema over TDS (INFORMATION_SCHEMA — no data). Any token/connect failure degrades to
            // Reachable=false with a scrubbed message rather than throwing across the door.
            try
            {
                var token = await EntraToken.AcquireSqlAsync(authMode, null, CancellationToken.None, tenantId).ConfigureAwait(false);
                var schema = await FabricSqlSchema.ReadAsync(probe.Server, probe.Database, token, CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(schema.Error)) { probe.Reachable = false; probe.Error = "source unreachable — " + schema.Error; return probe; }

                var match = schema.Tables.FirstOrDefault(x =>
                                (string.IsNullOrEmpty(probe.SchemaName) || string.Equals(x.Schema, probe.SchemaName, StringComparison.OrdinalIgnoreCase))
                                && string.Equals(x.Name, probe.Entity, StringComparison.OrdinalIgnoreCase))
                            ?? schema.Tables.FirstOrDefault(x => string.Equals(x.Name, probe.Entity, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    probe.Reachable = false;
                    probe.Error = $"source unreachable — table '{(string.IsNullOrEmpty(probe.SchemaName) ? "" : probe.SchemaName + ".")}{probe.Entity}' was not found at {probe.Server}/{probe.Database}.";
                    return probe;
                }

                probe.Columns = match.Columns.Select(c => new SourceColumn { Name = c.Name, DataType = c.DataType, SqlType = c.SqlType }).ToArray();
                probe.SchemaName = match.Schema; probe.Entity = match.Name;
                probe.Reachable = true;
                probe.Method = "fabric-sql";
                return probe;
            }
            catch (Exception ex)
            {
                probe.Reachable = false;
                probe.Error = "source unreachable — " + ScrubSchemaError(ex.Message);
                return probe;
            }
        }

        public async Task<SchemaDiff> DiffSchemaAsync(string tableRef, SourceColumn[] sourceColumns, string authMode, string tenantId)
        {
            var s = _sessions.Require();
            var view = await s.ReadAsync(m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t))
                    return (Name: (string)null, Ref: tableRef, Cols: (ColumnRow[])null, Error: $"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");
                var rows = BuildColumnRows(m).Where(r => string.Equals(r.Table, t.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                return (Name: t.Name, Ref: ObjectRefs.For(t), Cols: rows, Error: (string)null);
            });
            if (view.Error != null) return new SchemaDiff { TableRef = tableRef, Reachable = false, Error = view.Error };

            // Supplied source columns (a synthesized/manual diff — the offline path + tests) skip the live probe.
            if (sourceColumns != null && sourceColumns.Length > 0)
                return SchemaSync.Diff(view.Ref, view.Name, view.Cols, sourceColumns, "supplied");

            var src = await GetSourceSchemaAsync(tableRef, authMode, tenantId);
            if (!src.Reachable) return new SchemaDiff { Table = view.Name, TableRef = view.Ref, Reachable = false, Error = src.Error };
            return SchemaSync.Diff(view.Ref, view.Name, view.Cols, src.Columns, src.Method ?? "fabric-sql");
        }

        public async Task<ApplySchemaResult> ApplySchemaUpdateAsync(string tableRef, SchemaUpdateItem[] items, string origin)
        {
            var s = _sessions.Require();
            var list = (items ?? Array.Empty<SchemaUpdateItem>()).Where(i => i != null && !string.IsNullOrWhiteSpace(i.Column)).ToList();
            if (list.Count == 0) return new ApplySchemaResult { Revision = s.Revision, Changed = false };

            var applied = new List<string>();
            var skipped = new List<string>();
            int added = 0, removed = 0, retyped = 0;

            var rev = await s.MutateAsync(origin, $"update schema {tableRef}", m =>
            {
                if (!(ObjectRefs.Resolve(m, tableRef) is Table t))
                    throw new InvalidOperationException($"{tableRef} is not a table — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");

                Column Find(string name) => t.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

                // Apply order: retype, then add, then remove — removals last so a retype/add can't collide with a
                // just-deleted column, and the whole batch is one undo step (a per-item failure is skipped, not fatal).
                foreach (var i in list.Where(x => Kind(x.Change) == "TypeChanged"))
                {
                    try
                    {
                        var c = Find(i.Column);
                        if (c == null) { skipped.Add($"{i.Column}: not found in the model"); continue; }
                        if (!(c is DataColumn dc)) { skipped.Add($"{i.Column}: not a data column (a calculated column's type comes from its DAX)"); continue; }
                        if (string.IsNullOrWhiteSpace(i.DataType)) { skipped.Add($"{i.Column}: no target data type"); continue; }
                        var dt = ParseDataType(i.DataType);
                        if (dc.DataType != dt) { dc.DataType = dt; retyped++; applied.Add($"retype {i.Column} -> {i.DataType}"); }
                    }
                    catch (Exception ex) { skipped.Add($"{i.Column}: {ex.Message}"); }
                }

                foreach (var i in list.Where(x => Kind(x.Change) == "Added"))
                {
                    try
                    {
                        if (Find(i.Column) != null) { skipped.Add($"{i.Column}: already in the model"); continue; }
                        CreateColumnCore(t, i.Column, string.IsNullOrWhiteSpace(i.DataType) ? "String" : i.DataType, string.IsNullOrWhiteSpace(i.SourceColumn) ? i.Column : i.SourceColumn);
                        added++; applied.Add($"add {i.Column} ({(string.IsNullOrWhiteSpace(i.DataType) ? "String" : i.DataType)})");
                    }
                    catch (Exception ex) { skipped.Add($"{i.Column}: {ex.Message}"); }
                }

                foreach (var i in list.Where(x => Kind(x.Change) == "Removed"))
                {
                    try
                    {
                        var c = Find(i.Column);
                        if (c == null) { skipped.Add($"{i.Column}: not found in the model"); continue; }
                        if (c is CalculatedColumn) { skipped.Add($"{i.Column}: calculated column — remove it explicitly, not via schema sync"); continue; }
                        c.Delete(); removed++; applied.Add($"remove {i.Column}");
                    }
                    catch (Exception ex) { skipped.Add($"{i.Column}: {ex.Message}"); }
                }
            });

            return new ApplySchemaResult
            {
                Revision = rev,
                Changed = added + removed + retyped > 0,
                Added = added, Removed = removed, Retyped = retyped,
                Applied = applied.ToArray(), Skipped = skipped.ToArray(),
            };
        }

        // Normalise an accepted change label (case/spacing tolerant) to the canonical kind.
        private static string Kind(string change)
        {
            switch ((change ?? "").Trim().ToLowerInvariant())
            {
                case "added": case "add": return "Added";
                case "removed": case "remove": return "Removed";
                case "typechanged": case "retype": case "type": return "TypeChanged";
                default: return "";
            }
        }

        // Defensive scrub for a source-read exception message (the token rides out-of-band, but never leak one).
        private static string ScrubSchemaError(string message) =>
            string.IsNullOrEmpty(message) ? message
                : Regex.Replace(message, @"(?i)\b(password|pwd|access[_ ]?token)\s*=\s*[^;]*", "$1=***");
    }
}
