using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Semanticus.Engine
{
    /// <summary>One device-local event in a connection's timeline (a connect, an open, an account switch, a sign-in).
    /// Holds NO credentials — only the WHERE (endpoint/dataset), the account UPN a surface may display, and the
    /// outcome. It is a convenience log, so it never carries a token, secret, or the model's contents.</summary>
    public sealed class ConnectionHistoryEvent
    {
        public string Id { get; set; }             // the connection record id this event belongs to (null if unresolved)
        public string Kind { get; set; }           // connect | open | switch | signin
        public string Account { get; set; }        // the account UPN in play, when known (never a secret)
        public string Endpoint { get; set; }
        public string Database { get; set; }
        public string TenantId { get; set; }
        public bool Ok { get; set; }               // outcome — an attempt that failed is still worth remembering
        public string Detail { get; set; }         // short human note (e.g. why a switch happened); never a credential
        public string WhenUtc { get; set; }
    }

    /// <summary>
    /// The connection timeline. Deliberately a SEPARATE file beside the registry (<c>connection-history.json</c>, not
    /// inside <c>connections.json</c>): opens/switches write frequently, and the registry file is the permissions-bearing
    /// target list the agent-gate reads under a cross-process lock — a chatty history write must never contend with, or
    /// risk corrupting, the file governance depends on. Fail-open on read (a broken log yields no history, never an
    /// error) and never throws on append (a log write must not fail the connect it is a convenience for).
    /// </summary>
    public static class ConnectionHistory
    {
        // Two ceilings: a per-connection cap keeps one busy target from crowding the timeline, and a total cap bounds
        // the file. Newest-first, so the cap always evicts the oldest.
        public const int MaxPerConnection = 25;
        public const int MaxTotal = 500;
        private const string FileName = "connection-history.json";

        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        // Beside the registry: share its root (and its test RootOverride), so tests and the real home stay in lockstep.
        private static string FilePath() => Path.Combine(ConnectionRegistry.Root(), FileName);

        /// <summary>Record one event. Best-effort: any failure is swallowed — the caller already succeeded (or failed)
        /// at the real work, and a timeline write does not get to change that outcome.</summary>
        public static void Append(ConnectionHistoryEvent ev)
        {
            if (ev == null) return;
            ev.WhenUtc ??= DateTimeOffset.UtcNow.ToString("O");
            // Defence at the persistence boundary: a timeline event's endpoint + database are a WHERE, never a
            // credential. Reduce BOTH to safe coordinates and drop any pasted Password=/token= so a chatty log can never
            // leak a secret. FAIL CLOSED: an input that carried a credential we couldn't reduce to a safe address is
            // logged as a redacted placeholder, never the raw bytes (a failed open of "…?token=SECRET" must not persist
            // the secret). Detail is a human note; scrub it too in case a caller ever interpolates a coordinate.
            var coords = ConnectionInput.Parse(ev.Endpoint, ev.Database);
            ev.Endpoint = coords.Safe ? coords.Endpoint : ConnectionInput.Redacted;
            ev.Database = coords.Database;
            if (XmlaAuthHint.ContainsSecret(ev.Endpoint)) ev.Endpoint = ConnectionInput.Redacted;
            ev.Detail = XmlaAuthHint.Scrub(ev.Detail);
            try
            {
                lock (Gate)
                using (AcquireLock())
                {
                    var all = ReadForWrite();
                    all.Insert(0, ev);
                    Save(all);
                }
            }
            catch { /* a convenience log never fails the connection it is a log of */ }
        }

        /// <summary>The timeline newest-first, optionally filtered to one connection. Read-only; a parse failure just
        /// yields an empty list (the pre-history behaviour), never an error.</summary>
        public static IReadOnlyList<ConnectionHistoryEvent> List(string connectionId = null)
        {
            lock (Gate)
            {
                var all = ReadForDisplay();
                return string.IsNullOrWhiteSpace(connectionId)
                    ? all
                    : all.Where(e => string.Equals(e.Id, connectionId, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        // ---- persistence -------------------------------------------------------------------------------------

        private static IDisposable AcquireLock()
        {
            Directory.CreateDirectory(ConnectionRegistry.Root());
            var lockPath = FilePath() + ".lock";
            for (var i = 0; i < 100; i++)
            {
                try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) { System.Threading.Thread.Sleep(15); }
            }
            // The history is best-effort, so contention degrades to "skip this event" (Append swallows the throw) rather
            // than blocking or corrupting — unlike the registry, losing one timeline entry is harmless.
            throw new IOException("Could not acquire the connection-history lock.");
        }

        private static List<ConnectionHistoryEvent> ReadForDisplay()
        {
            try
            {
                var p = FilePath();
                if (!File.Exists(p)) return new List<ConnectionHistoryEvent>();
                return JsonSerializer.Deserialize<List<ConnectionHistoryEvent>>(File.ReadAllText(p), JsonOpts) ?? new List<ConnectionHistoryEvent>();
            }
            catch { return new List<ConnectionHistoryEvent>(); }
        }

        // On the write path an unreadable log is simply discarded (moved aside) rather than preserved: unlike the
        // registry it carries no governance data, so a fresh log is the right recovery — but we still keep the bytes
        // for a curious user rather than deleting them outright.
        private static List<ConnectionHistoryEvent> ReadForWrite()
        {
            var p = FilePath();
            if (!File.Exists(p)) return new List<ConnectionHistoryEvent>();
            try
            {
                return JsonSerializer.Deserialize<List<ConnectionHistoryEvent>>(File.ReadAllText(p), JsonOpts) ?? new List<ConnectionHistoryEvent>();
            }
            catch
            {
                try { File.Move(p, p + ".corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8)); } catch { /* best-effort */ }
                return new List<ConnectionHistoryEvent>();
            }
        }

        private static void Save(List<ConnectionHistoryEvent> all)
        {
            // Enforce the per-connection cap first (keep the newest N of each), then the global cap over what remains.
            // Order is preserved (newest-first) because GroupBy is stable and we re-sort by the original position.
            if (all.Count > MaxTotal || all.GroupBy(e => e.Id ?? "").Any(g => g.Count() > MaxPerConnection))
            {
                var perConnCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var kept = new List<ConnectionHistoryEvent>(Math.Min(all.Count, MaxTotal));
                foreach (var e in all)   // already newest-first
                {
                    var key = e.Id ?? "";
                    perConnCount.TryGetValue(key, out var c);
                    if (c >= MaxPerConnection) continue;
                    perConnCount[key] = c + 1;
                    kept.Add(e);
                    if (kept.Count >= MaxTotal) break;
                }
                all = kept;
            }

            // Serialization-boundary scrub (CRITICAL 1): redact any secret from EVERY string field of every event before
            // the write — a secret-shaped account/tenant/detail, or a field added later. Endpoint/database are already
            // reduced in Append; this is the chokepoint that a future field cannot regress past.
            foreach (var e in all) XmlaAuthHint.ScrubStringProperties(e);

            Directory.CreateDirectory(ConnectionRegistry.Root());
            var tmp = FilePath() + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOpts));
            File.Move(tmp, FilePath(), overwrite: true);
        }
    }
}
