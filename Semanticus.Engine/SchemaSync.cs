using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    /// <summary>
    /// Pure (connection-free) helpers behind the Update-Schema feature: parse a partition's M to find the SQL
    /// source it reads (server / database / schema / table), and diff a table's SOURCE columns against the model's
    /// current columns. Kept separate from <see cref="LocalEngine"/> so BOTH the M-parser and the diff logic are
    /// unit-testable without a live source (the source-read leg is the only part that needs a reachable endpoint).
    /// </summary>
    public static class SchemaSync
    {
        // Extract the SQL source coordinates a partition's M query reads. Handles the two canonical shapes the
        // Power Query SQL connector emits:
        //   Sql.Database("server","db")   { [Schema="dbo", Item="Sales"] }[Data]
        //   Sql.Databases("server")       { [Name="db"] }[Data]  { [Schema="dbo", Item="Sales"] }[Data]
        // schema/item are optional (an Entity/Direct-Lake partition carries them on the partition itself, not in M).
        // Returns true iff at least a server + database were found — the minimum to reach a Fabric/SQL endpoint.
        public static bool TryParseSqlSource(string m, out string server, out string database, out string schema, out string item)
        {
            server = database = schema = item = null;
            if (string.IsNullOrWhiteSpace(m)) return false;

            // A native-query source ([Query="SELECT …"]) has no navigable Item — INFORMATION_SCHEMA can't describe an
            // arbitrary query, so we deliberately DON'T treat it as reachable here (the caller reports the limitation).
            var db2 = Regex.Match(m, @"Sql\.Database\s*\(\s*""([^""]*)""\s*,\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (db2.Success) { server = db2.Groups[1].Value; database = db2.Groups[2].Value; }
            else
            {
                var dbs = Regex.Match(m, @"Sql\.Databases\s*\(\s*""([^""]*)""", RegexOptions.IgnoreCase);
                if (dbs.Success)
                {
                    server = dbs.Groups[1].Value;
                    var name = Regex.Match(m, @"\[\s*Name\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
                    if (name.Success) database = name.Groups[1].Value;
                }
            }

            var sch = Regex.Match(m, @"Schema\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (sch.Success) schema = sch.Groups[1].Value;
            var it = Regex.Match(m, @"Item\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (it.Success) item = it.Groups[1].Value;

            return !string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(database);
        }

        /// <summary>
        /// Diff the SOURCE columns against the model's current columns for one table. Added = at the source but not
        /// in the model; Removed = a DATA column in the model the source dropped; TypeChanged = a DATA column whose
        /// source type no longer matches. Calculated columns are DAX-derived (no source column) so they're never
        /// flagged Removed/TypeChanged, and an Added item is never proposed for a name already taken (even by a calc
        /// column). Pure — the source columns are supplied (probed live, or synthesized by a test / the mock UI).
        /// </summary>
        public static SchemaDiff Diff(string tableRef, string tableName, IEnumerable<ColumnRow> modelColumns,
            IEnumerable<SourceColumn> sourceColumns, string source)
        {
            var model = (modelColumns ?? Enumerable.Empty<ColumnRow>()).Where(c => c != null && !string.IsNullOrEmpty(c.Name)).ToList();
            var src = (sourceColumns ?? Enumerable.Empty<SourceColumn>()).Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name)).ToList();

            var modelNames = new HashSet<string>(model.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var srcByName = new Dictionary<string, SourceColumn>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in src) srcByName[c.Name] = c;   // last wins (defensive against a dup at the source)

            var items = new List<SchemaDiffItem>();

            // Added — a source column with no model counterpart.
            foreach (var c in src)
                if (!modelNames.Contains(c.Name))
                    items.Add(new SchemaDiffItem { Id = "add:" + c.Name, Change = "Added", Column = c.Name, SourceType = c.DataType, SqlType = c.SqlType });

            // Removed / TypeChanged — only DATA columns are source-driven.
            foreach (var c in model)
            {
                if (c.IsCalculated)
                {
                    if (!srcByName.ContainsKey(c.Name)) continue;   // a calc column absent from the source is normal, not a diff
                    continue;
                }
                if (!srcByName.TryGetValue(c.Name, out var sc))
                    items.Add(new SchemaDiffItem { Id = "remove:" + c.Name, Change = "Removed", Column = c.Name, ColumnRef = c.Ref, ModelType = c.DataType });
                else if (!TypeMatches(c.DataType, sc.DataType))
                    items.Add(new SchemaDiffItem { Id = "retype:" + c.Name, Change = "TypeChanged", Column = c.Name, ColumnRef = c.Ref, ModelType = c.DataType, SourceType = sc.DataType, SqlType = sc.SqlType });
            }

            // Stable UI ordering: Added, then TypeChanged, then Removed; by column name within each.
            var order = new Dictionary<string, int> { ["Added"] = 0, ["TypeChanged"] = 1, ["Removed"] = 2 };
            var ordered = items.OrderBy(i => order[i.Change]).ThenBy(i => i.Column, StringComparer.OrdinalIgnoreCase).ToArray();

            return new SchemaDiff
            {
                Table = tableName,
                TableRef = tableRef,
                Reachable = true,
                Source = source,
                Items = ordered,
                Added = ordered.Count(i => i.Change == "Added"),
                Removed = ordered.Count(i => i.Change == "Removed"),
                TypeChanged = ordered.Count(i => i.Change == "TypeChanged"),
            };
        }

        // TOM data-type names compared case-insensitively. Both sides speak the same 6-type vocabulary (the source
        // type is already mapped from SQL via FabricSqlSchema.MapSqlType), so an equal name = no change.
        private static bool TypeMatches(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
