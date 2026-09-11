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

        // The placeholder written when an input could not be reduced to ONE safe address , because it carried a
        // credential we will not echo, or because its URI authority names two hosts (see HasAmbiguousAuthority). It is
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
                return HasAmbiguousAuthority(s)
                    ? new Coordinates(Redacted, explicitDb, hadCredential, safe: false)
                    : new Coordinates(SafeCoordinate(s), explicitDb, hadCredential, safe: true);

            // Separate the key=value components QUOTE-AWARE, and fail closed when the quoting cannot be read. A ';'
            // INSIDE a quoted value is part of that value, not a separator , that is the whole reason the dialect has
            // quoting. See SplitComponents for the mechanism this replaced and what it lost.
            string address = null, catalog = null;
            var ambiguous = !SplitComponents(s, out var components);
            foreach (var part in components)
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part.Substring(0, eq).Trim().ToLowerInvariant();
                var val = part.Substring(eq + 1).Trim();
                // A value that OPENS with a quote must be a whole quoted value. Unquote returns it unchanged when it is
                // not (unterminated, or bytes trailing the closing quote), and where such a value ends , so which host
                // it names , is exactly what we cannot know. FAIL CLOSED rather than half-read it.
                if (OpensQuoted(val) && ReferenceEquals(Unquote(val), val)) { ambiguous = true; break; }
                if (Array.IndexOf(AddressKeys, key) >= 0)
                {
                    var value = Unquote(val);
                    // Two address keys naming DIFFERENT hosts: this parser takes the first, the ADO.NET dialect takes
                    // the last, so the host connected to would depend on who read the string. Same disagreement, so the
                    // same answer , refuse. An exact repeat names one host and is kept.
                    if (address != null && !string.Equals(address, value, StringComparison.Ordinal)) { ambiguous = true; break; }
                    address ??= value;
                }
                else if (catalog == null && Array.IndexOf(CatalogKeys, key) >= 0) catalog = Unquote(val);
                // Every other key — including Password / User ID / access token — is deliberately DROPPED here.
            }

            var resolvedDb = explicitDb ?? SafeCatalog(catalog);
            if (ambiguous)
                return new Coordinates(Redacted, resolvedDb, hadCredential, safe: false);
            if (!string.IsNullOrWhiteSpace(address))
                // Recognized coordinates. The address value can still embed a query-string secret
                // ("powerbi://foo?token=SECRET"); SafeCoordinate redacts it so the raw secret never survives.
                //
                // A ';' can only reach this token from a QUOTED value now , SplitComponents ends a component at every
                // unquoted one , and inside quotes it is DATA, not a separator: the quotes already ended the component,
                // so there is no connection-string tail left to drop. Both ways of acting on it lose the caller's
                // address. In the AUTHORITY it is the spoof HasAmbiguousAuthority names (two hosts, one string). After
                // the authority, XmlaAuthHint.SafeEndpoint would cut the token at that ';' and hand on a TRUNCATED
                // path, which on a powerbi:// address is a different WORKSPACE than the caller wrote. Neither may be
                // done silently, so the shape is refused whole and the callers stop before any credential, ticket or
                // write. (An unquoted tail is unaffected: it never gets here.)
                return address.IndexOf(';') >= 0
                    ? new Coordinates(Redacted, resolvedDb, hadCredential, safe: false)
                    : new Coordinates(SafeCoordinate(address), resolvedDb, hadCredential, safe: true);

            // '='-bearing input with NO recognized address key — not a connection string we can reduce to safe
            // coordinates. FAIL CLOSED: if it carried a credential we must NEVER echo the raw bytes, so return a
            // redacted placeholder + Safe=false (remember refuses; history logs the placeholder). A benign '='-bearing
            // input with no credential (e.g. a file path with an '=' in it) passes through, still defensively scrubbed.
            return hadCredential || HasAmbiguousAuthority(s)
                ? new Coordinates(Redacted, resolvedDb, hadCredential, safe: false)
                : new Coordinates(SafeCoordinate(s), resolvedDb, hadCredential, safe: true);
        }

        // Reduce ONE address token to a persist-safe coordinate: drop any connection-string tail (from the first ';')
        // and redact any residual key=value / JWT secret in the URL/query. NEVER returns the raw input — a residual
        // "?token=SECRET" is redacted to "?token=***" (secret gone) rather than kept. Delegates to the same scrubber
        // that guards every surfaced endpoint, so the rules stay in one place.
        internal static string Unquote(string val)
        {
            if (string.IsNullOrEmpty(val) || val.Length < 2) return val ?? "";
            var q = val[0];
            if ((q != '"' && q != '\'') || val[val.Length - 1] != q) return val;
            var inner = val.Substring(1, val.Length - 2);
            return inner.Replace(new string(q, 2), q.ToString());
        }

        // True when a value OPENS with a quote, so the dialect says the whole value is quoted and Unquote must be able
        // to resolve it. Paired with Unquote returning its argument unchanged (same reference) when it cannot.
        private static bool OpensQuoted(string val) =>
            !string.IsNullOrEmpty(val) && (val[0] == '"' || val[0] == '\'');

        /// <summary>Split a connection string into its key=value components, QUOTE-AWARE, returning false when the
        /// quoting cannot be read at all (a value opens a quote that never closes).
        ///
        /// What this replaced, and why. The parser used <c>s.Split(';')</c>, which knows nothing about quoting, so it
        /// cut a QUOTED value at a ';' that lives inside it. A quoted address containing a semicolon followed by
        /// user-info syntax was reduced to its leading fragment, and the
        /// closing quote plus the real host were discarded as if they were another component. Nothing downstream could
        /// recover that , <see cref="HasAmbiguousAuthority"/> is handed ONE address token and the ';' that made the
        /// input ambiguous had already been split away , so an authority spoof was accepted as a Safe coordinate that
        /// was neither the address the caller wrote nor a reduction of it, and could be connected to and persisted.
        /// The quoting is not decoration: it is the dialect's own way of saying "this ';' is data, not a separator".
        ///
        /// Scope: only a value that OPENS with a quote (after '=' and any spaces) starts a quoted run, and inside one a
        /// doubled quote ("" / '') is an escaped quote and does not end it , the same escape
        /// <see cref="XmlaAuthHint"/> resolves and <see cref="Unquote"/> reverses. A quote anywhere else is an ordinary
        /// character, so nothing that parsed before parses differently unless it was quoted, which is the defect.
        ///
        /// The .NET DbConnectionStringBuilder was the obvious reuse and does not fit this seam: it REJECTS (throws)
        /// '='-bearing inputs this parser deliberately lets through unrecognized and still scrubbed, and it resolves a
        /// repeated key last-wins, which would silently pick a host rather than refuse an ambiguous one.</summary>
        private static bool SplitComponents(string s, out List<string> components)
        {
            components = new List<string>();
            var start = 0;
            var afterEquals = false;   // past this component's first '='
            var atValueStart = false;  // nothing but spaces seen since that '='
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == ';')
                {
                    var part = s.Substring(start, i - start).Trim();
                    if (part.Length > 0) components.Add(part);
                    start = i + 1;
                    afterEquals = false;
                    atValueStart = false;
                    continue;
                }
                if (c == '=' && !afterEquals) { afterEquals = true; atValueStart = true; continue; }
                if (afterEquals && atValueStart && (c == '"' || c == '\''))
                {
                    var close = CloseQuote(s, i, c);
                    if (close < 0) { components = new List<string>(); return false; }   // never closed: unreadable
                    i = close;
                    atValueStart = false;
                    continue;
                }
                if (!char.IsWhiteSpace(c)) atValueStart = false;
            }
            var last = s.Substring(start).Trim();
            if (last.Length > 0) components.Add(last);
            return true;
        }

        // Index of the quote that closes the run opened at <paramref name="open"/>, or -1 if it is never closed. A
        // doubled quote inside the run is an escaped quote and keeps the run open.
        private static int CloseQuote(string s, int open, char q)
        {
            for (var i = open + 1; i < s.Length; i++)
            {
                if (s[i] != q) continue;
                if (i + 1 < s.Length && s[i + 1] == q) { i++; continue; }
                return i;
            }
            return -1;
        }

        /// <summary>Does this ONE address token name two different hosts depending on who reads it? A URI's destination
        /// is its AUTHORITY, and <see cref="XmlaAuthHint.SafeEndpoint"/> is deliberately lossy at the first ';' , right
        /// for a connection-string tail, and a silent RETARGET for a URI whose ';' sits inside the authority. In an
        /// authority spelled `name;` then '@' then a different host, the ';' makes the leading token user-info and the
        /// trailing name the real host, yet the truncation keeps the scheme plus the leading token and throws the real
        /// host away. <c>LiveDeploy.IsLocalEndpoint</c> preserves the authority and classifies the real host (pinned
        /// REMOTE as R21-R25 in LocalEndpointTests), so the address CLASSIFIED at intake and the coordinate CONNECTED to
        /// and PERSISTED would name different hosts. That disagreement is the defect, and neither reading may be acted
        /// on silently: not the truncated one (the caller's own authority says otherwise), and not the trailing host
        /// (that is the host such a spelling exists to hide, and we would mint an Entra token for it). So this fails
        /// CLOSED , the coordinate is unsafe, callers refuse before any credential, intent ticket or write, and nothing
        /// is retargeted. Scope is exactly the authority: a ';' at or after the path/query/fragment cannot move the
        /// host, and a connection string's own ';' separators are split off before this ever sees a token.</summary>
        internal static bool HasAmbiguousAuthority(string address)
        {
            var e = Unquote((address ?? "").Trim());
            var scheme = e.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0) return false;                       // no URI authority to lose; ';' is a tail separator
            var authority = e.Substring(scheme + 3);
            var cut = authority.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0) authority = authority.Substring(0, cut);
            return authority.IndexOf(';') >= 0;
        }

        private static string SafeCoordinate(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "";
            var e = XmlaAuthHint.SafeEndpoint(Unquote(address.Trim()));
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
