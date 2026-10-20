using System;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>
    /// Which SQL servers the engine may send a sign-in token to. A check, a model's partitions and a teammate's
    /// suite.jsonl all arrive from a file somebody else wrote, and any of them can name a server its sender owns, so a
    /// token is minted only for a server the person chose on THIS machine: a saved SQL source (which also covers the open
    /// model's own SQL endpoint, because the endpoint is a link to a saved source). Saved sources live outside the model
    /// and outside any repo, so a clone cannot add one. A server a person types into a call of their own, in this session,
    /// is also theirs; that is decided by the caller (it knows the origin), not here.
    /// </summary>
    internal static class SqlHostTrust
    {
        /// <summary>The server as a host name alone: no tcp: prefix, no port, no case. "TCP:Contoso.database.windows.net,1433" and
        /// "contoso.database.windows.net" are the same place. An IPv6 address is the whole address in its short form, so
        /// "tcp:[2001:db8::1],1433" and "2001:DB8:0:0:0:0:0:1" are one place and "2001:db8::2" is another. An address that cannot be read
        /// gives "", which no check ever trusts: it is never cut down to its first group. A named pipe keeps its whole path and
        /// the lpc: and admin: forms keep their prefix, so none of them shares a key with another server.</summary>
        internal static string HostOf(string server)
        {
            var s = (server ?? "").Trim();
            if (s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4).TrimStart();
            // Named pipes, shared memory and the admin connection also put a colon after their prefix. The prefix stays in the key, so
            // "np:" and "tcp:" of one name are two ways in, and the first colon is never where the host is cut.
            if (s.StartsWith("np:", StringComparison.OrdinalIgnoreCase))
            {
                // The pipe path is the address: two pipe names on one machine are two keys.
                var pipe = s.Substring(3).Trim();
                return pipe.Length == 0 ? "" : "np:" + pipe.ToLowerInvariant();
            }
            foreach (var prefix in new[] { "lpc:", "admin:" })
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var inner = HostOf(s.Substring(prefix.Length));
                    return inner.Length == 0 || inner.StartsWith("np:") || inner.StartsWith("lpc:") || inner.StartsWith("admin:") ? "" : prefix + inner;
                }
            if (s.StartsWith("["))
            {
                // A bracketed address ends at its closing bracket; a port or a named instance after it is not part of the host.
                var close = s.IndexOf(']');
                return close < 0 ? "" : Ipv6Of(s.Substring(1, close - 1));
            }
            // More than one colon cannot be host:port, so it is an IPv6 address with no brackets. SQL writes its port after a comma.
            if (s.IndexOf(':') != s.LastIndexOf(':'))
            {
                var end = s.IndexOfAny(new[] { ',', '\\' });
                return Ipv6Of(end >= 0 ? s.Substring(0, end) : s);
            }
            var cut = s.IndexOfAny(new[] { ',', ':' });
            if (cut >= 0) s = s.Substring(0, cut);
            // A named instance (host\instance) is still that host.
            var slash = s.IndexOf('\\');
            if (slash >= 0) s = s.Substring(0, slash);
            return s.Trim().TrimEnd('.').ToLowerInvariant();
        }

        private static string Ipv6Of(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || text.IndexOf(':') < 0) return "";
            return System.Net.IPAddress.TryParse(text, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? address.ToString().ToLowerInvariant()
                : "";
        }

        /// <summary>True when a saved SQL source on this machine points at this host.</summary>
        internal static bool IsSaved(string server)
        {
            var host = HostOf(server);
            if (host.Length == 0) return false;
            return SqlSourceRegistry.List().Any(r => HostOf(r.Server) == host);
        }

        // Microsoft's own SQL hosts. A sign-in token sent to one of these reaches a Microsoft gateway, which the owner of a
        // database on it cannot read from, so a model that names one needs no question. Public, US Gov and China cloud forms.
        private static readonly string[] MicrosoftSuffixes =
        {
            ".database.windows.net", ".database.usgovcloudapi.net", ".database.chinacloudapi.cn",
            ".datawarehouse.fabric.microsoft.com", ".datawarehouse.pbidedicated.windows.net",
            ".sql.azuresynapse.net", ".sql.azuresynapse.usgovcloudapi.net", ".sql.azuresynapse.azure.cn",
            ".pbidedicated.windows.net", ".pbidedicated.usgovcloudapi.net", ".pbidedicated.chinacloudapi.cn",
            ".fabric.microsoft.com", ".fabric.microsoft.us", ".fabric.microsoft.cn",
        };

        /// <summary>True when the whole host (case, trailing dot and port ignored) ends in a Microsoft SQL suffix on a label
        /// boundary. "contoso.database.windows.net.attacker.net" ends in attacker.net, so it does not pass; neither does a bare
        /// suffix with no name in front of it, nor an address that is not a plain host name.</summary>
        internal static bool IsMicrosoftHost(string server)
        {
            var host = HostOf(server);
            if (host.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(host, @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")) return false;
            return MicrosoftSuffixes.Any(s => host.Length > s.Length && host.EndsWith(s, StringComparison.Ordinal));
        }

        private static readonly System.Collections.Generic.HashSet<string> SessionTrusted = new System.Collections.Generic.HashSet<string>();
        private static readonly System.Collections.Generic.HashSet<string> SessionDeclined = new System.Collections.Generic.HashSet<string>();

        /// <summary>A host the person typed into a call of their own, or said yes to, in this session. Gone when the engine restarts.</summary>
        internal static void TrustForSession(string server)
        {
            var host = HostOf(server);
            if (host.Length == 0) return;
            lock (SessionTrusted) { SessionTrusted.Add(host); SessionDeclined.Remove(host); }
        }

        internal static bool IsTrustedThisSession(string server)
        {
            var host = HostOf(server);
            lock (SessionTrusted) return host.Length > 0 && SessionTrusted.Contains(host);
        }

        internal static void DeclineForSession(string server)
        {
            var host = HostOf(server);
            if (host.Length > 0) lock (SessionTrusted) SessionDeclined.Add(host);
        }

        internal static bool WasDeclinedThisSession(string server)
        {
            var host = HostOf(server);
            lock (SessionTrusted) return host.Length > 0 && SessionDeclined.Contains(host);
        }

        /// <summary>Forget what this session decided. Tests only.</summary>
        internal static void ResetSessionForTest() { lock (SessionTrusted) { SessionTrusted.Clear(); SessionDeclined.Clear(); } }

        /// <summary>A check's rule: a saved source or a Microsoft SQL host. A host typed in this session is the caller's call there.</summary>
        internal static bool IsSavedOrMicrosoft(string server) => IsSaved(server) || IsMicrosoftHost(server);

        /// <summary>May a sign-in token go to this server with no question: a saved source, a Microsoft SQL host, or one the person
        /// typed or accepted in this session.</summary>
        internal static bool MayReceiveToken(string server) => IsSaved(server) || IsMicrosoftHost(server) || IsTrustedThisSession(server);

        /// <summary>The plain words for a check that names a server the person has not saved. The host is shown as written.</summary>
        internal static string UnsavedWords(string server) =>
            $"This check names a server you have not saved: {server?.Trim()}. Open it to choose a source.";
    }

    /// <summary>
    /// A check's SQL may only read. It is one statement that starts with SELECT or WITH, with no statement after a semicolon
    /// and none of the words that change data or the database, read as whole words outside comments, quotes and brackets.
    /// A column called UpdateDate or [Delete Flag] is a name, not a word, so it is never refused.
    /// </summary>
    internal static class SqlReadOnlyGuard
    {
        internal const string Words = "This check's SQL changes data, so Semanticus will not run it. A check can only read.";

        private static readonly string[] Banned =
        {
            "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "ALTER", "CREATE", "TRUNCATE", "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY",
            // SELECT ... INTO makes a table, so it writes as surely as INSERT does.
            "INTO",
            // T-SQL needs no semicolon between statements, so any statement a check must never run is banned by its first word.
            "UPDATETEXT", "WRITETEXT", "BULK", "DBCC", "RECEIVE", "KILL", "BACKUP", "RESTORE",
            "OPENQUERY", "OPENROWSET", "OPENDATASOURCE", "SHUTDOWN", "RECONFIGURE", "SEND",
        };

        /// <summary>Null when the SQL is one read; otherwise the words to show.</summary>
        internal static string Refusal(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return null;   // an empty box has its own message elsewhere
            var code = StripQuotedAndComments(sql);
            var trimmed = code.Trim().TrimEnd(';').TrimEnd();
            if (trimmed.IndexOf(';') >= 0) return Words;
            var first = trimmed.TrimStart('(', ' ', '\t', '\r', '\n');
            if (!StartsWithWord(first, "SELECT") && !StartsWithWord(first, "WITH")) return Words;
            foreach (var word in WordsOf(trimmed))
                if (Array.Exists(Banned, b => string.Equals(b, word, StringComparison.OrdinalIgnoreCase))) return Words;
            return null;
        }

        private static bool StartsWithWord(string s, string word) =>
            s.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (s.Length == word.Length || !IsWordChar(s[word.Length]));

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

        private static System.Collections.Generic.IEnumerable<string> WordsOf(string code)
        {
            var i = 0;
            while (i < code.Length)
            {
                if (!IsWordChar(code[i])) { i++; continue; }
                var start = i;
                // T-SQL reads a number glued to a word as two tokens ("SELECT 1UPDATE t" is SELECT 1 UPDATE t), so a word that starts
                // with a digit ends its digit run at the first letter and the rest is a word of its own.
                if (char.IsDigit(code[i]))
                {
                    while (i < code.Length && char.IsDigit(code[i])) i++;
                    if (i < code.Length && IsWordChar(code[i]) && !char.IsDigit(code[i])) { yield return code.Substring(start, i - start); start = i; }
                    else if (i >= code.Length || !IsWordChar(code[i])) { yield return code.Substring(start, i - start); continue; }
                }
                while (i < code.Length && IsWordChar(code[i])) i++;
                yield return code.Substring(start, i - start);
            }
        }

        /// <summary>The SQL with every comment, string, "quoted name" and [bracketed name] replaced by a space, so what is left is
        /// only the words T-SQL itself reads. Block comments nest in T-SQL, so they nest here.</summary>
        private static string StripQuotedAndComments(string sql)
        {
            var sb = new System.Text.StringBuilder(sql.Length);
            var i = 0;
            while (i < sql.Length)
            {
                var c = sql[i];
                if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    // A line comment ends at a bare carriage return as well as at a line feed.
                    while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r') i++;
                    sb.Append(' ');
                }
                else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    var depth = 1; i += 2;
                    while (i < sql.Length && depth > 0)
                    {
                        if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; i += 2; }
                        else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { depth--; i += 2; }
                        else i++;
                    }
                    sb.Append(' ');
                }
                else if (c == '\'' || c == '"' || c == '[')
                {
                    var close = c == '[' ? ']' : c;
                    i++;
                    while (i < sql.Length)
                    {
                        if (sql[i] == close)
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }   // '' and ]] are an escaped quote
                            i++; break;
                        }
                        i++;
                    }
                    sb.Append(' ');
                }
                else { sb.Append(c); i++; }
            }
            return sb.ToString();
        }
    }
}
