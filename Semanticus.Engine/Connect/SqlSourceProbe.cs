using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>The outcome of testing one saved SQL source. <see cref="Note"/> is always present and always in plain
    /// words: a person reading "we could not sign in" must not have to read a provider stack trace to learn it.</summary>
    public sealed class SqlSourceTestResult
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public bool Ok { get; set; }
        public string Note { get; set; }
        public long ElapsedMs { get; set; }
        public string TestedUtc { get; set; }
    }

    /// <summary>
    /// Opens a connection with a saved source's OWN sign-in and asks it the smallest possible question. This is the
    /// "Test connection" behind a named SQL source, and it is the one place that proves the record is usable before a
    /// check depends on it.
    ///
    /// The sign-in reads the SOURCE's mode and tenant. That is the fix for what Astra recorded: a check authored with
    /// no mode reached <see cref="EntraToken.AcquireSqlAsync"/>, which reads an absent mode as azcli, so a person on a
    /// browser sign-in got a confusing failure from a CLI they had never run. The source states its mode out loud, and
    /// the helper is handed that, never a default.
    /// </summary>
    public static class SqlSourceProbe
    {
        /// <summary>The whole question: one constant value, no model object touched, nothing the user's identity
        /// cannot already read.</summary>
        public const string ProbeSql = "SELECT CAST(1 AS bigint) AS [semanticus_connection_test]";

        // Test seams (InternalsVisibleTo). The suite proves the RESULT SHAPE and the mode that reaches the token
        // helper with no live SQL and no tenant: a real acquisition here would open a browser on whatever machine
        // runs the tests. Null in production, the same idiom as EntraToken.DeviceCodePromptForTests.
        internal static Func<string, string, CancellationToken, Task<string>> TokenForTests;
        internal static Func<SqlSourceRecord, string, CancellationToken, Task<ResultSet>> QueryForTests;

        /// <summary>
        /// Sign in as the source says to, run <see cref="ProbeSql"/>, and record the outcome on the source. A failure
        /// is recorded as a failure: <see cref="SqlSourceRegistry.RecordTest"/> never rounds one up.
        /// </summary>
        /// <param name="disableInteractive">Agent origin: forbid a prompt, so a stale cache fails honestly instead of
        /// popping a browser on the user's machine.</param>
        public static async Task<SqlSourceTestResult> TestAsync(SqlSourceRecord source, bool disableInteractive, CancellationToken ct)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var result = new SqlSourceTestResult
            {
                Id = source.Id, Name = source.Name, Server = source.Server, Database = source.Database,
            };
            var sw = Stopwatch.StartNew();

            string token;
            try
            {
                token = TokenForTests != null
                    ? await TokenForTests(source.AuthMode, source.TenantId, ct).ConfigureAwait(false)
                    // The source's OWN mode and tenant, never a default.
                    : await EntraToken.AcquireSqlAsync(source.AuthMode, null, ct, source.TenantId, disableInteractive).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Finish(result, sw, false, "Semanticus could not sign in to this source. " + Scrub(ex.Message));
            }

            ResultSet rs;
            try
            {
                rs = QueryForTests != null
                    ? await QueryForTests(source, token, ct).ConfigureAwait(false)
                    : await FabricSqlQuery.ExecuteAsync(source.Server, source.Database, token, ProbeSql, 1, 30, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return Finish(result, sw, false, "Semanticus signed in, but the source did not answer. " + Scrub(ex.Message));
            }

            sw.Stop();
            if (rs == null)
                return Finish(result, sw, false, "Semanticus signed in, but the source did not answer.");
            if (!string.IsNullOrEmpty(rs.Error))
                return Finish(result, sw, false, "Semanticus signed in, but the source did not answer. " + Scrub(rs.Error));
            if (rs.Rows == null || rs.Rows.Length != 1 || rs.Columns == null || rs.Columns.Length != 1)
                return Finish(result, sw, false, "The source answered, but not with the single value the test asked for. Check that the database name is right.");

            return Finish(result, sw, true, $"Connected to {source.Database} on {source.Server}.");
        }

        private static SqlSourceTestResult Finish(SqlSourceTestResult result, Stopwatch sw, bool ok, string note)
        {
            result.Ok = ok;
            result.Note = note;
            result.ElapsedMs = sw.ElapsedMilliseconds;
            var stamped = SqlSourceRegistry.RecordTest(result.Id, ok);
            result.TestedUtc = stamped?.LastTestedUtc ?? DateTimeOffset.UtcNow.ToString("O");
            return result;
        }

        // Never let a raw connection string reach a note, a log or a wire (golden rule 1). The SQL token rides
        // out-of-band on SqlConnection.AccessToken, so it should not appear here at all; mask it anyway.
        private static string Scrub(string message) =>
            string.IsNullOrEmpty(message) ? "" : Regex.Replace(message, @"(?i)\b(password|pwd|access[_ ]?token)\s*=\s*[^;]*", "$1=***");
    }
}
