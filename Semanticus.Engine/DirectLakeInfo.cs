using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// Direct Lake detection, defined once so every read path classifies a model the same way.
    ///
    /// A Direct Lake model reads Parquet/Delta on demand, so its storage DMVs (VertiPaq sizes, DISCOVER_STORAGE
    /// ROWS_COUNT, COLUMNSTATISTICS cardinality) reflect only the columns currently RESIDENT in memory — not the
    /// model totals. Read paths use this to LABEL those numbers "partial" rather than report them as authoritative,
    /// which would silently mislead (e.g. "0 rows" for a billion-row table, or a falsely clean Copilot scorecard).
    ///
    /// Mirrors the vendored (internal) Table.IsDirectLakeTable() logic using public wrapper members, since that
    /// extension isn't visible across the assembly boundary.
    /// </summary>
    internal static class DirectLakeInfo
    {
        // A table is Direct Lake when it has an Entity (EntityPartitionSource) partition whose effective mode is
        // DirectLake — either explicitly, or via Mode==Default inheriting a model DefaultMode of DirectLake.
        public static bool IsTableDirectLake(Table t)
        {
            if (t == null) return false;
            var modelDefaultDL = t.Model != null && t.Model.DefaultMode == ModeType.DirectLake;
            return t.Partitions.Any(p => p.SourceType == PartitionSourceType.Entity
                && (p.Mode == ModeType.DirectLake || (modelDefaultDL && p.Mode == ModeType.Default)));
        }

        // Model scope: an explicit DirectLake DefaultMode (catches a new DL model with no partitions yet), OR any
        // Direct Lake table.
        public static bool IsModelDirectLake(Model m) =>
            m != null && (m.DefaultMode == ModeType.DirectLake || m.Tables.Any(IsTableDirectLake));
    }
}
