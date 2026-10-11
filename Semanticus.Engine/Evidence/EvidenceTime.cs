using System;
using System.Globalization;

namespace Semanticus.Engine.Evidence
{
    /// <summary>An evidence time as a person reads it (F16): "25 Sep 2026, 11:45 AM (UTC+10)". The document keeps its
    /// ISO UTC stamp for programs; this is only the words drawn from it. The zone is the machine's own unless a
    /// caller pins one (tests do, so a golden does not depend on where it runs). The offset is always shown, because
    /// a saved report travels to people in other zones.</summary>
    public static class EvidenceTime
    {
        public static string Readable(string isoUtc, TimeZoneInfo zone = null)
        {
            if (string.IsNullOrWhiteSpace(isoUtc)) return "Not recorded";
            if (!DateTimeOffset.TryParse(isoUtc.Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
                return isoUtc.Trim();
            var z = zone ?? TimeZoneInfo.Local;
            var local = TimeZoneInfo.ConvertTime(utc, z);
            var words = local.ToString("d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture);
            return words + " (" + Offset(local.Offset) + ")";
        }

        private static string Offset(TimeSpan offset)
        {
            if (offset == TimeSpan.Zero) return "UTC";
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            var abs = offset.Duration();
            return "UTC" + sign + abs.Hours.ToString(CultureInfo.InvariantCulture)
                + (abs.Minutes == 0 ? "" : ":" + abs.Minutes.ToString("00", CultureInfo.InvariantCulture));
        }
    }
}
