using System;
using System.Diagnostics;
using System.Threading.Tasks;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Engine
{
    /// <summary>Result of a Storage-Engine cache clear (an XMLA ClearCache command on the connected database).</summary>
    public sealed class ClearCacheResult
    {
        public bool Cleared { get; set; }
        public bool Local { get; set; }          // a local Power BI Desktop / loopback instance (no all-users impact)
        public string DataSource { get; set; }
        public string Database { get; set; }
        public long ElapsedMs { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }        // scrubbed
    }

    /// <summary>
    /// Clears the VertiPaq Storage-Engine cache for the connected database — the DAX-Studio "Clear Cache" primitive
    /// that makes cold-vs-warm benchmarking meaningful. Sends the documented XMLA ClearCache command via a raw TOM
    /// Server (the same second-connection pattern <see cref="DaxTrace"/> uses for traces). NON-DESTRUCTIVE: it only
    /// evicts the cache (the engine rebuilds it on the next query) — no data or metadata change. Needs admin/local
    /// access: works on a local Power BI Desktop (Windows auth); a cloud XMLA endpoint without an admin token can't
    /// connect here and degrades to a scrubbed error (Cleared=false), exactly like the trace features.
    /// </summary>
    public static class DaxCache
    {
        public static Task<ClearCacheResult> ClearAsync(LiveConnection live)
        {
            if (live == null) return Task.FromResult(new ClearCacheResult { Error = "Not connected." });
            return Task.Run(() => Clear(live));   // AMO/TOM is thread-affine — never touch it from the dispatcher
        }

        private static ClearCacheResult Clear(LiveConnection live)
        {
            var sw = Stopwatch.StartNew();
            TOM.Server server = null;
            try
            {
                server = live.ConnectTraceServer();   // authenticated (reuses the live token) — local Windows auth or cloud XMLA bearer
                // Resolve the real database id: prefer the catalog the live connection bound to, else the only db.
                var db = (!string.IsNullOrEmpty(live.Database) ? server.Databases.FindByName(live.Database) : null)
                         ?? (server.Databases.Count == 1 ? server.Databases[0] : null)
                         ?? throw new InvalidOperationException("Could not resolve the database to clear on '" + live.DataSource + "'.");
                server.Execute(ClearCacheCommand(db.ID));
                sw.Stop();
                return new ClearCacheResult { Cleared = true, DataSource = live.DataSource, Database = db.Name, ElapsedMs = sw.ElapsedMilliseconds };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new ClearCacheResult
                {
                    Cleared = false, DataSource = live.DataSource, Database = live.Database, ElapsedMs = sw.ElapsedMilliseconds,
                    Error = "Clear cache failed: " + Scrub(ex.Message),
                    Note = "Clearing the Storage-Engine cache needs a local Power BI Desktop or an admin XMLA endpoint.",
                };
            }
            finally { try { server?.Disconnect(); } catch { } try { server?.Dispose(); } catch { } }
        }

        /// <summary>The documented XMLA ClearCache command for one database id — pure/buildable so it's unit-testable.</summary>
        public static string ClearCacheCommand(string databaseId) =>
            "<ClearCache xmlns=\"http://schemas.microsoft.com/analysisservices/2003/engine\">" +
            "<Object><DatabaseID>" + System.Security.SecurityElement.Escape(databaseId) + "</DatabaseID></Object></ClearCache>";

        // Defense-in-depth: strip any Password=/PWD= value before an error can surface (it's fanned to every Studio client).
        private static string Scrub(string message) =>
            string.IsNullOrEmpty(message) ? message
                : System.Text.RegularExpressions.Regex.Replace(message, @"(?i)\b(password|pwd)\s*=\s*[^;]*", "$1=***");
    }
}
