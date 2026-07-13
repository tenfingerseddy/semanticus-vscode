using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Semanticus.Engine
{
    public sealed class FabricColumn
    {
        public string Name { get; set; }
        public string DataType { get; set; }       // mapped to a TOM data type (String|Int64|Decimal|Double|DateTime|Boolean)
        public string SqlType { get; set; }        // the raw INFORMATION_SCHEMA DATA_TYPE
        public bool IsNullable { get; set; }
        public int Ordinal { get; set; }
    }

    public sealed class FabricTable
    {
        public string Schema { get; set; }
        public string Name { get; set; }
        public string[] KeyColumns { get; set; } = Array.Empty<string>();
        public FabricColumn[] Columns { get; set; } = Array.Empty<FabricColumn>();
    }

    public sealed class FabricForeignKey
    {
        public string FromSchema { get; set; }
        public string FromTable { get; set; }
        public string FromColumn { get; set; }
        public string ToSchema { get; set; }
        public string ToTable { get; set; }
        public string ToColumn { get; set; }
    }

    /// <summary>A deterministic snapshot of a Fabric SQL endpoint's schema (read from INFORMATION_SCHEMA).</summary>
    public sealed class FabricSchema
    {
        public string Server { get; set; }
        public string Database { get; set; }
        public FabricTable[] Tables { get; set; } = Array.Empty<FabricTable>();
        public FabricForeignKey[] ForeignKeys { get; set; } = Array.Empty<FabricForeignKey>();
        public string Error { get; set; }

        public static FabricSchema FromError(string e) => new FabricSchema { Error = e };
    }

    /// <summary>
    /// Reads a Fabric SQL endpoint's schema (tables, columns + types, PK/FK) over TDS using the engine's existing
    /// Entra token (SQL scope). A deterministic, read-only data read — the FIRST non-XMLA connection in the engine,
    /// but no new trust boundary (same user identity, no Anthropic credentials, no inference). Fabric Warehouse /
    /// Lakehouse SQL endpoints declare keys only as NOT ENFORCED (and may omit them entirely), so missing PK/FK is
    /// treated as "none declared," not an error.
    /// </summary>
    public static class FabricSqlSchema
    {
        public static async Task<FabricSchema> ReadAsync(string server, string database, string accessToken, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(server)) return FabricSchema.FromError("A Fabric SQL endpoint (server) is required.");
            if (string.IsNullOrWhiteSpace(database)) return FabricSchema.FromError("A database is required.");
            try
            {
                var csb = new SqlConnectionStringBuilder
                {
                    DataSource = server,
                    InitialCatalog = database,
                    Encrypt = SqlConnectionEncryptOption.Mandatory,
                    ConnectTimeout = 30,
                    ApplicationName = "Semanticus",
                };
                using var conn = new SqlConnection(csb.ConnectionString) { AccessToken = accessToken };
                await conn.OpenAsync(ct).ConfigureAwait(false);

                var columns = new List<(string Schema, string Table, FabricColumn Col)>();
                await Query(conn, ct,
                    "SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION",
                    r => columns.Add((Str(r, "TABLE_SCHEMA"), Str(r, "TABLE_NAME"), new FabricColumn
                    {
                        Name = Str(r, "COLUMN_NAME"),
                        Ordinal = Int(r, "ORDINAL_POSITION"),
                        SqlType = Str(r, "DATA_TYPE"),
                        DataType = MapSqlType(Str(r, "DATA_TYPE")),
                        IsNullable = string.Equals(Str(r, "IS_NULLABLE"), "YES", StringComparison.OrdinalIgnoreCase),
                    })));

                var tableNames = new List<(string Schema, string Table)>();
                await Query(conn, ct,
                    "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_SCHEMA, TABLE_NAME",
                    r => tableNames.Add((Str(r, "TABLE_SCHEMA"), Str(r, "TABLE_NAME"))));

                var keys = new List<(string Schema, string Table, string Column)>();
                await Query(conn, ct,
                    @"SELECT kcu.TABLE_SCHEMA, kcu.TABLE_NAME, kcu.COLUMN_NAME
                      FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                      JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                        ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND tc.CONSTRAINT_SCHEMA = kcu.CONSTRAINT_SCHEMA
                      WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'",
                    r => keys.Add((Str(r, "TABLE_SCHEMA"), Str(r, "TABLE_NAME"), Str(r, "COLUMN_NAME"))));

                var fks = new List<FabricForeignKey>();
                await Query(conn, ct,
                    @"SELECT fk.TABLE_SCHEMA AS FromSchema, fk.TABLE_NAME AS FromTable, fk.COLUMN_NAME AS FromColumn,
                             pk.TABLE_SCHEMA AS ToSchema, pk.TABLE_NAME AS ToTable, pk.COLUMN_NAME AS ToColumn
                      FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
                      JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE fk
                        ON rc.CONSTRAINT_NAME = fk.CONSTRAINT_NAME AND rc.CONSTRAINT_SCHEMA = fk.CONSTRAINT_SCHEMA
                      JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE pk
                        ON rc.UNIQUE_CONSTRAINT_NAME = pk.CONSTRAINT_NAME AND rc.UNIQUE_CONSTRAINT_SCHEMA = pk.CONSTRAINT_SCHEMA
                       AND fk.ORDINAL_POSITION = pk.ORDINAL_POSITION",
                    r => fks.Add(new FabricForeignKey
                    {
                        FromSchema = Str(r, "FromSchema"), FromTable = Str(r, "FromTable"), FromColumn = Str(r, "FromColumn"),
                        ToSchema = Str(r, "ToSchema"), ToTable = Str(r, "ToTable"), ToColumn = Str(r, "ToColumn"),
                    }));

                var colsByTable = columns.GroupBy(x => (x.Schema, x.Table));
                var keysByTable = keys.GroupBy(x => (x.Schema, x.Table)).ToDictionary(g => g.Key, g => g.Select(x => x.Column).ToArray());
                var tables = tableNames.Select(tn => new FabricTable
                {
                    Schema = tn.Schema,
                    Name = tn.Table,
                    KeyColumns = keysByTable.TryGetValue((tn.Schema, tn.Table), out var kc) ? kc : Array.Empty<string>(),
                    Columns = columns.Where(c => c.Schema == tn.Schema && c.Table == tn.Table).OrderBy(c => c.Col.Ordinal).Select(c => c.Col).ToArray(),
                }).ToArray();

                return new FabricSchema { Server = server, Database = database, Tables = tables, ForeignKeys = fks.ToArray() };
            }
            catch (Exception ex) { return FabricSchema.FromError(Scrub(ex.Message)); }
        }

        private static async Task Query(SqlConnection conn, CancellationToken ct, string sql, Action<SqlDataReader> onRow)
        {
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
            using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) onRow(r);
        }

        private static string Str(SqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i)); }
        private static int Int(SqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i)); }

        // INFORMATION_SCHEMA DATA_TYPE (T-SQL) -> TOM data type. Testable without a connection.
        internal static string MapSqlType(string sqlType)
        {
            switch ((sqlType ?? "").Trim().ToLowerInvariant())
            {
                case "bit": return "Boolean";
                case "tinyint": case "smallint": case "int": case "bigint": return "Int64";
                case "decimal": case "numeric": case "money": case "smallmoney": return "Decimal";
                case "float": case "real": return "Double";
                case "date": case "datetime": case "datetime2": case "smalldatetime": case "datetimeoffset": case "time": return "DateTime";
                default: return "String"; // char/varchar/nvarchar/text/uniqueidentifier/xml/binary/...
            }
        }

        // Never let a raw connection error / connection string reach a caller/log/RPC. The SQL token rides on
        // SqlConnection.AccessToken (out-of-band), so it shouldn't appear in messages, but scrub defensively.
        private static string Scrub(string message) =>
            string.IsNullOrEmpty(message) ? message
                : System.Text.RegularExpressions.Regex.Replace(message, @"(?i)\b(password|pwd|access[_ ]?token)\s*=\s*[^;]*", "$1=***");
    }
}
