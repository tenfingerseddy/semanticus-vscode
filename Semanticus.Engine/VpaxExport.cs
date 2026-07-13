using System.IO;
using Adomd = Microsoft.AnalysisServices.AdomdClient;
using Dax.Model.Extractor;
using Dax.Vpax.Tools;

namespace Semanticus.Engine
{
    /// <summary>
    /// Exports the open model to a VertiPaq-Analyzer <c>.vpax</c> file via Microsoft/SQLBI's official Dax.Vpax +
    /// Dax.Model.Extractor — the interchange format for VertiPaq Analyzer / DAX Studio / the SQLBI ecosystem.
    /// <see cref="Write"/> is metadata-only (offline). <see cref="WriteWithStats"/> additionally reads LIVE VertiPaq
    /// storage statistics (column sizes / cardinality / segments) — the primary value of a .vpax — from a live
    /// connection. The engine falls back to metadata-only if the live read is unavailable.
    /// </summary>
    internal static class VpaxExport
    {
        private const string App = "Semanticus";
        private const string Ver = "1.0";

        public static int Write(string path, Microsoft.AnalysisServices.Tabular.Database db)
        {
            var daxModel = TomExtractor.GetDaxModel(db.Model, App, Ver);
            using (var fs = File.Create(path))
                VpaxTools.ExportVpax(fs, daxModel);
            return daxModel.Tables.Count;
        }

        /// <summary>Metadata (from TOM) PLUS live storage statistics read over <paramref name="connectionString"/>
        /// (a token-bearing XMLA connection string). Opens its OWN short-lived AdomdConnection so it never contends
        /// with the engine's single-threaded live connection.</summary>
        public static int WriteWithStats(string path, Microsoft.AnalysisServices.Tabular.Database db, string connectionString, int sampleRows)
        {
            var daxModel = TomExtractor.GetDaxModel(db.Model, App, Ver);
            using (var conn = new Adomd.AdomdConnection(connectionString))
            {
                conn.Open();
                // The 3-arg overload defaults DirectLakeExtractionMode to ResidentOnly — the safe default (it never
                // forces a Direct Lake column into memory just to size it). The CS0618 advisory only matters for DL
                // models extracted with a NON-ResidentOnly mode, which we deliberately don't do.
#pragma warning disable CS0618
                StatExtractor.UpdateStatisticsModel(daxModel, conn, sampleRows);
#pragma warning restore CS0618
            }
            using (var fs = File.Create(path))
                VpaxTools.ExportVpax(fs, daxModel);
            return daxModel.Tables.Count;
        }
    }
}
