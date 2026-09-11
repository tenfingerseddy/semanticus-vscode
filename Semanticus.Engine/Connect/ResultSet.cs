using System;

namespace Semanticus.Engine
{
    public sealed class ColumnDef
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }

    /// <summary>The shared result envelope returned verbatim to both the webview and MCP tool results.</summary>
    public sealed class ResultSet
    {
        public ColumnDef[] Columns { get; set; } = Array.Empty<ColumnDef>();
        public object[][] Rows { get; set; } = Array.Empty<object[]>();
        public int RowCount { get; set; }
        public bool Truncated { get; set; }
        public long ElapsedMs { get; set; }
        public string Error { get; set; }
        /// <summary>The query text that produced this result, so a later editor change cannot be paired with the wrong number.</summary>
        public string Query { get; set; }
        /// <summary>True when the query was stopped before it finished. <see cref="Error"/> is <see cref="StoppedMessage"/>.</summary>
        public bool Cancelled { get; set; }

        public const string StoppedMessage = "The query was stopped.";

        /// <summary>Plain announcement for both doors. A capped grid must never read as a complete count.</summary>
        public static string RowAnnouncement(int rowCount, bool truncated)
        {
            if (rowCount == 1 && !truncated) return "1 row";
            var rows = rowCount + (rowCount == 1 ? " row" : " rows");
            return truncated ? rows + " shown. More rows exist." : rows;
        }

        public static ResultSet FromCancelled() => new ResultSet { Error = StoppedMessage, Cancelled = true };

        /// <summary>Cut a result to maxRows and mark it capped. Also stamps <see cref="Query"/> when missing.</summary>
        public static ResultSet ApplyCap(ResultSet source, int maxRows, string query = null)
        {
            if (source == null) return source;
            if (string.IsNullOrEmpty(source.Query) && !string.IsNullOrEmpty(query)) source.Query = query;
            if (source.Cancelled || !string.IsNullOrEmpty(source.Error)) return source;
            var cap = maxRows <= 0 ? 10000 : maxRows;
            var rows = source.Rows ?? Array.Empty<object[]>();
            if (rows.Length > cap)
            {
                var kept = new object[cap][];
                Array.Copy(rows, kept, cap);
                source.Rows = kept;
                source.RowCount = cap;
                source.Truncated = true;
            }
            else if (source.RowCount <= 0) source.RowCount = rows.Length;
            return source;
        }
        /// <summary>True when <see cref="Error"/> is an agent-policy REFUSAL (the query was never executed), not a
        /// query failure. A structured marker set only at the GuardAgent folds, so downstream wrappers (the interview
        /// scorer) can pick the right recovery advice without sniffing message text — "get approval / ask a human"
        /// vs "fix the DAX".</summary>
        public bool PolicyRefused { get; set; }
        /// <summary>The exact pending approval created by a policy refusal, when the target is configured to Ask.</summary>
        public string ApprovalId { get; set; }
        /// <summary>True when <see cref="Error"/> is an XMLA/Entra AUTHENTICATION failure (not signed in / wrong
        /// tenant / rejected token), classified from the raw exception at the connection layer — NOT sniffed from the
        /// scrubbed message downstream. Mirrors <see cref="PolicyRefused"/>: a typed marker so the interview scorer can
        /// tell "sign in" from "fix the DAX" without misreading a DAX ERROR("Unauthorized") as an auth failure.</summary>
        public bool AuthFailed { get; set; }

        public static ResultSet FromError(string error) => new ResultSet { Error = DaxErrorText.Plain(error) };
        public static ResultSet FromRefusal(string reason, string approvalId = null) =>
            new ResultSet { Error = reason, PolicyRefused = true, ApprovalId = approvalId };
    }

    /// <summary>Stop a running live query. Both doors call the same engine method.</summary>
    public sealed class CancelQueryResult
    {
        public bool Stopped { get; set; }
        public string Message { get; set; }
    }

    public sealed class ConnectionStatus
    {
        public bool Connected { get; set; }
        public string Kind { get; set; }        // "xmla" | "local"
        public string DataSource { get; set; }
        public string Database { get; set; }
        public string ConnectionId { get; set; }
        public string Message { get; set; }
        // The account (UPN) this connect signed in as, when known — so the MCP door sees the identity in play, not just
        // the endpoint. Null for azcli/serviceprincipal/token (no named account) — honestly "account unknown".
        public string Account { get; set; }
    }

    public sealed class LocalInstance
    {
        public int Port { get; set; }
        public string Title { get; set; }
        public string DataSource => "localhost:" + Port;
    }
}
