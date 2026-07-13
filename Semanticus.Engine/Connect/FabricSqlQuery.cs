using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Semanticus.Engine
{
    /// <summary>
    /// THE engine's single SQL executor against a Fabric SQL endpoint (TDS), using the signed-in user's existing Entra
    /// token (SQL scope) — the same identity, credential path, and trust boundary as <see cref="FabricSqlSchema"/>.
    /// Kept <c>internal</c> on purpose: a future user-facing <c>run_sql</c> op sits ON TOP of this seam rather than
    /// spinning up a second SQL stack. Ground-truth reconciliation SQL arrives from the user; the ambient table-count
    /// check is the narrow exception and emits only an engine-escaped <c>COUNT_BIG(*)</c> over detected identifiers.
    ///
    /// The SQL runs read-only against the source endpoint AS THE USER'S OWN IDENTITY (the engine holds no elevated
    /// credential — it can only ever read what the signed-in user already can). Governance of whether an AGENT may run
    /// it is the standard QueryData permission gate on the SQL target (the permissions tab) — enforced by the runner,
    /// not here.
    ///
    /// A query failure, timeout, or cancellation returns an <see cref="ResultSet"/> with <see cref="ResultSet.Error"/>
    /// set (never an empty successful set — contract f). A single cell the provider cannot hand us (a decimal(38,...)
    /// overflowing .NET decimal on GetValue) is substituted with <see cref="ReconcileValues.UnsupportedCell"/> so ONE
    /// unrepresentable value degrades to a per-side Unsupported verdict instead of aborting the whole run (contract b).
    /// </summary>
    internal static class FabricSqlQuery
    {
        internal static async Task<ResultSet> ExecuteAsync(
            string server, string database, string accessToken, string sql, int maxRows, int commandTimeoutSeconds, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(server)) return ResultSet.FromError("A Fabric SQL endpoint (server) is required to run the ground-truth SQL.");
            if (string.IsNullOrWhiteSpace(database)) return ResultSet.FromError("A database is required to run the ground-truth SQL.");
            if (string.IsNullOrWhiteSpace(sql)) return ResultSet.FromError("No SQL query text supplied — the ground-truth SQL is required (the engine never authors it).");

            var cap = maxRows <= 0 ? 10000 : maxRows;
            var sw = Stopwatch.StartNew();
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

                ColumnDef[] cols;
                var rows = new List<object[]>();
                var truncated = false;
                using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeoutSeconds <= 0 ? 120 : commandTimeoutSeconds })
                using (var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    cols = Enumerable.Range(0, rdr.FieldCount)
                        .Select(idx => new ColumnDef { Name = rdr.GetName(idx), Type = SafeTypeName(rdr, idx) })
                        .ToArray();
                    while (await rdr.ReadAsync(ct).ConfigureAwait(false))
                    {
                        if (rows.Count >= cap) { truncated = true; break; }
                        var r = new object[rdr.FieldCount];
                        for (var idx = 0; idx < rdr.FieldCount; idx++) r[idx] = ReadCell(rdr, idx);
                        rows.Add(r);
                    }
                }
                sw.Stop();
                return new ResultSet { Columns = cols, Rows = rows.ToArray(), RowCount = rows.Count, Truncated = truncated, ElapsedMs = sw.ElapsedMilliseconds };
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Never let a raw connection error / connection string reach a caller, log, or RPC (golden rule #1).
                return new ResultSet { Error = Scrub(ex.Message), ElapsedMs = sw.ElapsedMilliseconds };
            }
        }

        // Read one cell defensively: a NULL is a real present-NULL (null); a value the provider cannot materialise as
        // a CLR value (a SQL decimal(38,...) exceeding .NET decimal) is marked unsupported for THIS cell rather than
        // throwing and losing the whole result set (contract b — preclassify at the coercion boundary).
        private static object ReadCell(SqlDataReader rdr, int i)
        {
            try { return rdr.IsDBNull(i) ? null : rdr.GetValue(i); }
            catch (OverflowException) { return ReconcileValues.UnsupportedCell; }
            catch (InvalidCastException) { return ReconcileValues.UnsupportedCell; }
        }

        private static string SafeTypeName(SqlDataReader rdr, int i)
        {
            try { return rdr.GetFieldType(i)?.Name; } catch { return null; }
        }

        // The SQL token rides on SqlConnection.AccessToken (out-of-band), so it shouldn't appear in messages — scrub
        // defensively anyway (any password=/pwd=/access_token= in a message is masked).
        private static string Scrub(string message) =>
            string.IsNullOrEmpty(message) ? message
                : System.Text.RegularExpressions.Regex.Replace(message, @"(?i)\b(password|pwd|access[_ ]?token)\s*=\s*[^;]*", "$1=***");
    }
}
