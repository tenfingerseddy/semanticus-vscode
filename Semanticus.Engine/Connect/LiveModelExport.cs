using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Engine
{
    /// <summary>
    /// Reads a deployed model's metadata from a live XMLA endpoint (Power BI / Fabric / Azure AS) and
    /// serializes it to a local .bim snapshot, so the rest of the workbench opens it through the proven
    /// file path. TOM/AMO requires the Entra token via <c>Server.AccessToken</c> (the connection-string
    /// <c>Password=</c> form is ADOMD-only and is rejected by AMO's managed-auth "authenticators" chain),
    /// so we connect the Server ourselves with the token, then serialize. This only READS metadata out —
    /// it never deploys/writes back to the server.
    /// </summary>
    public static class LiveModelExport
    {
        public sealed class Snapshot
        {
            public string BimPath { get; set; }
            public string DatabaseName { get; set; }
            public int DatabaseCount { get; set; }
            public string[] DatabaseNames { get; set; }
        }

        private const string TempDirName = "semanticus-live";

        /// <summary>Test seam: redirect the snapshot root so tests never sweep the real %TEMP%.</summary>
        public static string TempRootOverride { get; set; }

        public static string TempRoot => TempRootOverride ?? Path.Combine(Path.GetTempPath(), TempDirName);

        /// <summary>The marker an engine drops in a snapshot dir it is actively using. Holds the owning process id.</summary>
        internal const string InUseMarker = ".inuse";

        /// <summary>Claim a snapshot dir for this process, so another engine's sweep leaves it alone while we hold it.</summary>
        public static void MarkInUse(string dir)
        {
            try { File.WriteAllText(Path.Combine(dir, InUseMarker), Environment.ProcessId.ToString()); } catch { }
        }

        // Is the dir claimed by a process that is still running? A crashed engine leaves its marker behind, so the pid
        // must be checked, not merely the marker's presence — otherwise nothing would ever be reclaimed after a crash.
        private static bool HeldByALiveProcess(string dir)
        {
            try
            {
                var marker = Path.Combine(dir, InUseMarker);
                if (!File.Exists(marker)) return false;
                if (!int.TryParse(File.ReadAllText(marker).Trim(), out var pid)) return false;
                if (pid == Environment.ProcessId) return true;
                using (System.Diagnostics.Process.GetProcessById(pid)) return true;
            }
            catch (ArgumentException) { return false; }   // no such process — the owner died
            catch { return true; }                        // can't tell ⇒ assume held (never delete on a guess)
        }

        /// <summary>
        /// Delete abandoned snapshot dirs. Every live/local open writes the target's FULL model metadata here — measures,
        /// M queries, role filters, datasource connection strings — and nothing ever removed them (3,490 files / 2.4 GB
        /// of client production metadata observed on one machine; issue #122).
        ///
        /// A SECOND engine process (the VS Code extension and the MCP server each own one) may hold a snapshot this one
        /// knows nothing about, and an open-but-idle session never rewrites its files — so age ALONE is not a safe
        /// signal. A dir claimed by a live process is skipped outright; only then does <paramref name="olderThan"/>
        /// decide, measured from the newest write anywhere inside. <paramref name="keep"/> is this engine's own dir.
        /// </summary>
        public static int SweepStale(TimeSpan olderThan, string keep = null)
        {
            var root = TempRoot;
            if (!Directory.Exists(root)) return 0;

            var cutoff = DateTime.UtcNow - olderThan;
            var keepFull = string.IsNullOrEmpty(keep) ? null : Norm(keep);
            var swept = 0;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (keepFull != null && string.Equals(Norm(dir), keepFull, StringComparison.OrdinalIgnoreCase)) continue;
                    if (HeldByALiveProcess(dir)) continue;
                    // Newest write ANYWHERE inside: a snapshot saved back to must not look stale just because the dir
                    // node kept its creation time.
                    var last = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Select(File.GetLastWriteTimeUtc)
                        .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(dir))
                        .Max();
                    if (last >= cutoff) continue;
                    Directory.Delete(dir, true);
                    swept++;
                }
                catch { /* in use, or already gone — leave it and move on */ }
            }
            return swept;
        }

        /// <summary>Absolute, separator-normalised, no trailing separator — the only form path comparisons use here.</summary>
        internal static string Norm(string p) =>
            Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        public static Task<Snapshot> ToBimAsync(string endpoint, string database, string token, DateTimeOffset expiresOn) =>
            Task.Run(() =>
            {
                using var server = new TOM.Server();
                if (!string.IsNullOrEmpty(token))
                    server.AccessToken = new AS.AccessToken(token, expiresOn);  // AMO managed auth: token goes here, not Password=
                // No token → a local Analysis Services instance (Power BI Desktop): connect with the current
                // Windows identity that owns the process. AMO uses integrated auth when AccessToken is unset.
                server.Connect("Data Source=" + endpoint);
                return Serialize(server, database);
            });

        /// <summary>Snapshot a LOCAL Analysis Services instance (Power BI Desktop) — integrated Windows auth,
        /// no Entra token. Used by the unified "open local" path so the running model becomes editable in the
        /// tree (the SAME instance is also bound live for queries).</summary>
        public static Task<Snapshot> ToBimLocalAsync(string dataSource, string database) =>
            ToBimAsync(dataSource, database, token: null, expiresOn: default);

        // Pick the dataset (named, or the workspace's only/first), serialize it to a temp .bim, return the snapshot.
        private static Snapshot Serialize(TOM.Server server, string database)
        {
            var names = server.Databases.Cast<TOM.Database>().Select(d => d.Name).ToArray();
            if (names.Length == 0)
                throw new InvalidOperationException("The workspace has no datasets, or the signed-in account has no access to them.");

            TOM.Database db = string.IsNullOrWhiteSpace(database)
                ? server.Databases[0]
                : (server.Databases.FindByName(database)
                   ?? throw new InvalidOperationException($"Dataset '{database}' not found in the workspace. Available: {string.Join(", ", names)}"));

            // Live AMO metadata can arrive without the Power BI mode. Preserve that serialization boundary before
            // creating the snapshot, or Power BI-only properties fail later while Review Changes reads the file.
            if (db.CompatibilityMode != AS.CompatibilityMode.PowerBI)
                db.CompatibilityMode = AS.CompatibilityMode.PowerBI;
            var json = TOM.JsonSerializer.SerializeDatabase(db);
            var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var bimPath = Path.Combine(dir, Sanitize(db.Name) + ".bim");
            File.WriteAllText(bimPath, json);

            // Reclaim dirs abandoned by earlier runs — a crash, a kill, or any build before #122 shipped. Done HERE,
            // where a snapshot is actually taken, rather than at engine start-up: only a process that writes to this
            // root has business pruning it, and it keeps the sweep out of every test that constructs an engine.
            // Best-effort and off-thread; a snapshot is already written and must not wait on housekeeping.
            Task.Run(() => { try { SweepStale(TimeSpan.FromDays(1), keep: dir); } catch { } });

            try { server.Disconnect(); } catch { /* best-effort; the snapshot is already written */ }
            return new Snapshot { BimPath = bimPath, DatabaseName = db.Name, DatabaseCount = names.Length, DatabaseNames = names };
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "model";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
