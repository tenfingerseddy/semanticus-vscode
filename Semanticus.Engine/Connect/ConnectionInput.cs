using System;
using System.Collections.Generic;

namespace Semanticus.Engine
{
    /// <summary>
    /// Normalises whatever a caller hands us as an "endpoint" down to SAFE COORDINATES — the bare data-source address
    /// and the dataset — before any of it is used to connect OR persisted. Callers routinely paste a full connection
    /// string ("Data Source=powerbi://…;Initial Catalog=Sales;Password=&lt;token&gt;"), and two things must never happen:
    /// the credential must never reach connections.json / the history (a durable secret leak), and the address must not
    /// be double-prefixed by <see cref="LiveConnection.XmlaConnectionString"/> ("Data Source=Data Source=…", a broken
    /// connect). So every entry point parses the input HERE: it extracts the address + catalog, and drops any credential
    /// component (we mint our own Entra token; a pasted Password/token is never trusted or kept).
    ///
    /// FAIL CLOSED. The parser NEVER returns the raw input verbatim: a recognized address is redacted of any residual
    /// query-string secret; an '='-bearing shape we cannot reduce to a recognized address AND that carries a credential
    /// yields a redacted placeholder with <see cref="Coordinates.Safe"/> = false, so a persistence boundary can REFUSE
    /// the operation (remember) or log the placeholder (history) instead of writing bytes that might hide a secret. The
    /// separate database argument is sanitized the same way — a "Sales;Password=SECRET" never reaches disk.
    /// </summary>
    internal static class ConnectionInput
    {
        /// <summary>The clean coordinates parsed out of a bare endpoint OR a full connection string.
        /// <see cref="Endpoint"/> and <see cref="Database"/> hold NO credential (any secret has been dropped or
        /// redacted). <see cref="HadCredential"/> records whether the raw input carried one. <see cref="Safe"/> is
        /// false ONLY when the input could not be reduced to a recognized address without a credential — the endpoint
        /// is then a redacted placeholder and a persistence boundary must refuse (remember) or log the placeholder
        /// (history), never the raw input.</summary>
        public readonly struct Coordinates
        {
            public Coordinates(string endpoint, string database, bool hadCredential, bool safe)
            { Endpoint = endpoint; Database = database; HadCredential = hadCredential; Safe = safe; }
            public string Endpoint { get; }
            public string Database { get; }
            public bool HadCredential { get; }
            public bool Safe { get; }
        }

        // The placeholder written when an input carried a credential but could not be reduced to a safe address. It is
        // never a valid coordinate, so a persistence site that consults Safe never confuses it for a real endpoint.
        public const string Redacted = "(redacted)";

        // Keys that name the address / the dataset in an OLE DB / ADOMD connection string (case-insensitive).
        private static readonly string[] AddressKeys = { "data source", "datasource", "server", "address", "addr", "network address" };
        private static readonly string[] CatalogKeys = { "initial catalog", "catalog", "database" };

        /// <summary>Reduce an endpoint argument (which may be a bare address or a full connection string) plus an
        /// optional explicit database to safe coordinates. A connection-string input has its address + catalog pulled
        /// out and every credential dropped; a bare address passes through. The explicit <paramref name="database"/>
        /// always wins over a catalog embedded in the string (the caller was specific), and is itself sanitized.</summary>
        public static Coordinates Parse(string endpoint, string database = null)
        {
            var s = (endpoint ?? "").Trim();
            // A credential can hide in EITHER argument — a pasted connection string OR the separate database
            // ("Sales;Password=SECRET"). Flag both so a persistence boundary can refuse/redact honestly.
            var hadCredential = XmlaAuthHint.ContainsSecret(s) || XmlaAuthHint.ContainsSecret(database);
            var explicitDb = SafeCatalog(database);

            // A connection-string form carries '=' pairs (a bare powerbi:// / asazure:// address never does). Only then
            // do we parse; otherwise the address is already clean and passes through verbatim (no secret can hide with
            // no '=' present — ContainsSecret's key=value / JWT shapes all require one, and a bare JWT is scrubbed here).
            if (s.IndexOf('=') < 0)
                return new Coordinates(SafeCoordinate(s), explicitDb, hadCredential, safe: true);

            string address = null, catalog = null;
            foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part.Substring(0, eq).Trim().ToLowerInvariant();
                var val = part.Substring(eq + 1).Trim();
                if (address == null && Array.IndexOf(AddressKeys, key) >= 0) address = val;
                else if (catalog == null && Array.IndexOf(CatalogKeys, key) >= 0) catalog = val;
                // Every other key — including Password / User ID / access token — is deliberately DROPPED here.
            }

            var resolvedDb = explicitDb ?? SafeCatalog(catalog);
            if (!string.IsNullOrWhiteSpace(address))
                // Recognized coordinates. The address value can still embed a query-string secret
                // ("powerbi://foo?token=SECRET"); SafeCoordinate redacts it so the raw secret never survives.
                return new Coordinates(SafeCoordinate(address), resolvedDb, hadCredential, safe: true);

            // '='-bearing input with NO recognized address key — not a connection string we can reduce to safe
            // coordinates. FAIL CLOSED: if it carried a credential we must NEVER echo the raw bytes, so return a
            // redacted placeholder + Safe=false (remember refuses; history logs the placeholder). A benign '='-bearing
            // input with no credential (e.g. a file path with an '=' in it) passes through, still defensively scrubbed.
            return hadCredential
                ? new Coordinates(Redacted, resolvedDb, hadCredential, safe: false)
                : new Coordinates(SafeCoordinate(s), resolvedDb, hadCredential, safe: true);
        }

        // Reduce ONE address token to a persist-safe coordinate: drop any connection-string tail (from the first ';')
        // and redact any residual key=value / JWT secret in the URL/query. NEVER returns the raw input — a residual
        // "?token=SECRET" is redacted to "?token=***" (secret gone) rather than kept. Delegates to the same scrubber
        // that guards every surfaced endpoint, so the rules stay in one place.
        private static string SafeCoordinate(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "";
            var e = XmlaAuthHint.SafeEndpoint(address);
            return e == "(none)" ? "" : e;
        }

        // A dataset/catalog is a bare name, never a credential carrier. Cut ONLY a credential-bearing "key=value" tail
        // ("Sales;Password=SECRET" -> "Sales"), never a bare ';' / '?' / '&' — the round-3 unconditional cut corrupted
        // legitimate names ("Sales & Marketing" -> "Sales", a wrong-target open/remember risk — HIGH 3). Then redact any
        // residual secret and FAIL CLOSED: drop it (null) if what remains still trips the detector, rather than persist it.
        private static string SafeCatalog(string database)
        {
            if (string.IsNullOrWhiteSpace(database)) return null;
            var d = database.Trim();
            var cut = XmlaAuthHint.SuspectKeyValueIndex(d);   // only a real "…;Password=x" tail; a bare '&' in a name is kept
            if (cut >= 0) d = d.Substring(0, cut).Trim();
            d = XmlaAuthHint.Scrub(d);
            return string.IsNullOrWhiteSpace(d) || XmlaAuthHint.ContainsSecret(d) ? null : d;
        }
    }
}
