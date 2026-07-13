using System;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Engine
{
    /// <summary>
    /// Executes a partition "process"/refresh against a LIVE Analysis Services / Power BI XMLA model — the data
    /// counterpart of <see cref="LiveDeploy"/> (which is metadata-only). Connects a raw TOM Server (token via
    /// Server.AccessToken for cloud, integrated Windows auth for a local Power BI Desktop instance), issues
    /// Partition.RequestRefresh(type), and commits with a single Model.SaveChanges() — the atomic boundary.
    ///
    /// SAFETY: this is only ever reached on commit=true (RefreshPartitionAsync runs a dry run otherwise and never
    /// calls this). A SaveChanges failure writes nothing and is returned as an error (never thrown across the door),
    /// with any secret scrubbed. The refresh TYPE was already validated as partition-level by the caller.
    /// </summary>
    public static class LiveRefresh
    {
        /// <summary>Connect, refresh the one partition, save. Returns (committed, error). `error` is non-null iff
        /// nothing was committed. `token` empty = a local instance (Windows auth, no bearer token).</summary>
        public static (bool committed, string error) RefreshPartition(
            string tableName, string partitionName, string refreshTypeName,
            string endpoint, string database, string token, DateTimeOffset expiresOn)
        {
            try
            {
                // Parse the name to the raw TOM enum (the caller validated the name + partition-level validity; the
                // wrapper and TOM RefreshType share member names, so name-based parsing is value-mismatch-proof).
                var type = (TOM.RefreshType)Enum.Parse(typeof(TOM.RefreshType), refreshTypeName, ignoreCase: true);

                using var server = new TOM.Server();
                if (!string.IsNullOrEmpty(token)) server.AccessToken = new AS.AccessToken(token, expiresOn);
                server.Connect("Data Source=" + endpoint);

                var liveDb = server.Databases.FindByName(database)
                    ?? throw new InvalidOperationException($"Database '{database}' not found on the endpoint.");
                var live = liveDb.Model;
                var t = live.Tables.Find(tableName)
                    ?? throw new InvalidOperationException($"Table '{tableName}' not found on the live model.");
                var p = t.Partitions.Find(partitionName)
                    ?? throw new InvalidOperationException($"Partition '{partitionName}' not found on live table '{tableName}'.");

                p.RequestRefresh(type);
                live.SaveChanges();   // the single atomic boundary — sends the TMSL refresh sequence to the engine
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, "Refresh failed — nothing was committed: " + Scrub(ex.Message));
            }
        }

        // Strip any Password=/PWD= value from an error before it can surface (defense-in-depth; the connection string
        // here carries no token, but a nested provider error could).
        private static string Scrub(string message) =>
            string.IsNullOrEmpty(message) ? message
                : System.Text.RegularExpressions.Regex.Replace(message, @"(?i)\b(password|pwd)\s*=\s*[^;]*", "$1=***");
    }
}
