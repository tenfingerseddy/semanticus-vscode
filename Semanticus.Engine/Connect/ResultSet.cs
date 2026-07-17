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

        public static ResultSet FromError(string error) => new ResultSet { Error = error };
        public static ResultSet FromRefusal(string reason, string approvalId = null) =>
            new ResultSet { Error = reason, PolicyRefused = true, ApprovalId = approvalId };
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
