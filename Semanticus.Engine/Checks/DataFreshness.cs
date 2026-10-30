#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine.Checks;

public static partial class DataFreshness
{
    public const int PartitionCap = 10000;
    public const string ClockExpression = "ROW(\"__today\", TODAY())";
    // The server model's schema stamp: when a measure, table, column or relationship last changed, and how many exist. A publish or
    // any other edit made on the service moves it without moving a partition, so cached proofs must not outlive it. It rides inside
    // the freshness set (no extra command). INFO.MODEL's own stamps do not move when a measure changes, so they are not used.
    public const string SchemaStamp = "MAXX(INFO.MEASURES(), [ModifiedTime]) & \"|\" & COUNTROWS(INFO.MEASURES()) & \"|\" & MAXX(INFO.TABLES(), [ModifiedTime])"
        + " & \"|\" & COUNTROWS(INFO.TABLES()) & \"|\" & MAXX(INFO.COLUMNS(), [ModifiedTime]) & \"|\" & COUNTROWS(INFO.COLUMNS())"
        + " & \"|\" & MAXX(INFO.RELATIONSHIPS(), [ModifiedTime]) & \"|\" & COUNTROWS(INFO.RELATIONSHIPS())";
    public const string Expression = "SELECTCOLUMNS(INFO.PARTITIONS(), \"__partition\", [ID], \"__asof\", [RefreshedTime], \"__today\", TODAY(), \"__schema\", " + SchemaStamp
        + ", \"__mode\", [Mode], \"__defaultmode\", MAXX(INFO.MODEL(), [DefaultMode]))";

    // Hash the entire partition vector. MAX(refresh time) misses a change to an older partition.
    public static (string? DataAsOf, DateOnly? ServerToday) Read(ResultSet? set)
    {
        if (set == null || set.Error != null || set.Truncated || set.Rows.Length == 0) return (null, null);
        var serverToday = ReadServerDay(set);
        int Column(string name) => Array.FindIndex(set.Columns, c => c.Name == name || c.Name == "[" + name + "]");
        var partition = Column("__partition");
        var refreshed = Column("__asof");
        var today = Column("__today");
        var schema = Column("__schema");   // optional: the server model's schema stamp (SchemaStamp)
        var mode = Column("__mode");
        var defaultMode = Column("__defaultmode");
        if (partition < 0 || refreshed < 0 || today < 0) return (null, serverToday);
        try
        {
            bool RefreshTracksData(object[] row)
            {
                if (mode < 0 || row[mode] == null) return false;
                var effectiveMode = (ModeType)Convert.ToInt32(row[mode], CultureInfo.InvariantCulture);
                if (effectiveMode == ModeType.Default)
                {
                    if (defaultMode < 0 || row[defaultMode] == null) return false;
                    effectiveMode = (ModeType)Convert.ToInt32(row[defaultMode], CultureInfo.InvariantCulture);
                }
                return effectiveMode is ModeType.Import or ModeType.DirectLake;
            }
            // Source writes can change DirectQuery or Dual data without moving RefreshedTime.
            // All caches share this key; keep the server day available for clock-dependent checks.
            if (!set.Rows.All(RefreshTracksData)) return (null, serverToday);
            var entries = set.Rows.Select(row => new {
                Id = Convert.ToString(row[partition], CultureInfo.InvariantCulture),
                At = row[refreshed] == null ? null : Convert.ToDateTime(row[refreshed], CultureInfo.InvariantCulture).ToString("O", CultureInfo.InvariantCulture)
            }).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
            if (entries.Any(e => e.Id == null || e.At == null) || entries.Select(e => e.Id).Distinct().Count() != entries.Length)
                return (null, serverToday);
            var stamp = schema < 0 ? null : string.Join("\n", set.Rows.Select(row => Convert.ToString(row[schema], CultureInfo.InvariantCulture)).Distinct());
            var vector = stamp == null ? JsonSerializer.Serialize(entries) : JsonSerializer.Serialize(new { entries, stamp });
            return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(vector))), serverToday);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or IndexOutOfRangeException)
        {
            return (null, serverToday);
        }
    }

    // The clock does not depend on partition count, refresh times, modes or metadata rights.
    public static DateOnly? ReadServerDay(ResultSet? set)
    {
        if (set == null || set.Error != null || set.Truncated || set.Rows.Length == 0) return null;
        var today = Array.FindIndex(set.Columns, c => c.Name is "__today" or "[__today]");
        if (today < 0) return null;
        try
        {
            var days = set.Rows.Where(row => row[today] != null && row[today] is not DBNull)
                .Select(row => DateOnly.FromDateTime(Convert.ToDateTime(row[today], CultureInfo.InvariantCulture))).Distinct().ToArray();
            return days.Length == 1 ? days[0] : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
