using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Adomd = Microsoft.AnalysisServices.AdomdClient;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Engine
{
    /// <summary>
    /// A live ADOMD connection to an XMLA endpoint (Fabric / Power BI Premium / PPU) or a local
    /// instance, used for read-only DAX/DMV execution while editing TMDL offline (attached-readonly).
    /// All ADOMD access is serialized on a dedicated query thread (separate from the model writer),
    /// so a slow EVALUATE never blocks edits, and the connection is touched by one thread only.
    /// </summary>
    public sealed class LiveConnection : IDisposable
    {
        private readonly Adomd.AdomdConnection _conn;
        private readonly Func<string, ResultSet> _executeForTest;
        private string _connectionString;   // kept so server-side tracing can re-auth identically (token encapsulated)
        private readonly ModelDispatcher _queryThread = new ModelDispatcher();
        // A session-scoped trace sees every query on this XMLA session. Keep the trace setup, warm-up, real query
        // and teardown in one exclusive lane with ordinary ExecuteAsync calls so another UI/MCP request cannot
        // contaminate its timings, plan or evaluation log. Per connection, not global: unrelated models stay live.
        private readonly System.Threading.SemaphoreSlim _queryOperationGate = new System.Threading.SemaphoreSlim(1, 1);
        // Disconnect/swap can race a caller that already captured this object. Close admission first, let admitted
        // query/trace/cache work finish, and only then dispose ADOMD + its thread. A stale late call fails clearly.
        private readonly object _lifetimeGate = new object();
        private int _activeOperations;
        private bool _retired;
        private int _disposeStarted;

        public string Kind { get; }
        public string DataSource { get; }
        /// <summary>The account (UPN) this connection actually authenticated as, when known — set by the open path from
        /// the credential it built. This is the IDENTITY IN PLAY for a live connection; a status/session read reports
        /// THIS, never a registry last-used hint (which can be stale after a tenant-wide account switch). Null for
        /// azcli / serviceprincipal / local (integrated Windows) / token — honestly "account unknown".</summary>
        public string Account { get; set; }
        /// <summary>The resolved catalog (database) the connection is bound to — captured after Open (ADOMD
        /// resolves the real dataset even when open_live passed an empty Initial Catalog). Needed by ClearCache,
        /// whose XMLA command requires a &lt;DatabaseID&gt;. Empty until a successful open.</summary>
        public string Database { get; private set; }
        /// <summary>The query session's id (DISCOVER_SESSIONS) — captured after Open. Used to scope a session-level
        /// trace to OUR session so server timings / query plans / EVALUATEANDLOG work on a Power BI XMLA endpoint
        /// WITHOUT server-admin rights (a workspace member may trace their own session). Empty if unavailable.</summary>
        public string SessionId { get; private set; }
        /// <summary>The session's server process id (SPID), from DISCOVER_SESSIONS. The SPID is the ONLY trace column
        /// carried by EVERY query event — including DAXQueryPlan / DAXEvaluationLog, which do NOT carry the SessionID
        /// column — so it is the universal key for scoping a session trace (SessionID alone captures QueryEnd + SE
        /// timings but misses the query plan + EVALUATEANDLOG events). Empty if it could not be resolved.</summary>
        public string Spid { get; private set; }
        /// <summary>Diagnostic note on how (or whether) the SPID was resolved — surfaced in trace fallback messages
        /// while server-side tracing is brought up on cloud XMLA. Never contains a secret.</summary>
        public string SpidNote { get; private set; }
        /// <summary>The Power BI Desktop display-name STEM this LOCAL connection resolved to at connect time, when
        /// capturable (see <see cref="LiveOrigin.LocalName"/> for why it exists). Null for cloud XMLA, real SSAS,
        /// and uncapturable. Stamped once by connect_local/open_local; never re-derived.</summary>
        public string DesktopName { get; internal set; }
        /// <summary>The full .pbix PATH behind this LOCAL connection, when the owning Desktop's command line exposed
        /// it (see <see cref="LiveOrigin.LocalPath"/>). Stamped once by connect_local/open_local.</summary>
        public string DesktopPath { get; internal set; }

        private LiveConnection(string kind, string dataSource, Adomd.AdomdConnection conn, string connectionString,
            Func<string, ResultSet> executeForTest = null)
        {
            Kind = kind; DataSource = dataSource; _conn = conn; _connectionString = connectionString;
            _executeForTest = executeForTest;
        }

        public static string XmlaConnectionString(string endpoint, string database, string bearerToken)
        {
            var cs = $"Data Source={endpoint};Application Name=Semanticus";
            if (!string.IsNullOrEmpty(database)) cs += $";Initial Catalog={database}";   // empty = let the server pick its only/first dataset (open_live)
            if (!string.IsNullOrEmpty(bearerToken)) cs += $";Password={bearerToken}";
            return cs;
        }

        public static string LocalConnectionString(string dataSource, string database) =>
            string.IsNullOrEmpty(database)
                ? $"Data Source={dataSource};Application Name=Semanticus"
                : $"Data Source={dataSource};Initial Catalog={database};Application Name=Semanticus";

        public static async Task<LiveConnection> OpenAsync(string kind, string dataSource, string connectionString)
        {
            var conn = new Adomd.AdomdConnection(connectionString);
            var lc = new LiveConnection(kind, dataSource, conn, connectionString);
            try
            {
                // Capture the resolved catalog on the query thread right after Open (the connection is thread-affine).
                await lc._queryThread.RunAsync(() =>
                {
                    conn.Open();
                    try { lc.Database = conn.Database; } catch { }
                    try { lc.SessionId = conn.SessionID; } catch { }
                    lc.CaptureSpid(conn);   // best-effort SPID for the trace filter (the only key carried by every event)
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Open failed (bad endpoint / expired token / unreachable) — dispose the connection AND the
                // query thread we already spun up, otherwise every failed connect leaks a thread + a socket.
                lc.Dispose();
                // GOLDEN RULE #1: the connection string carries the bearer token as Password=. Re-throw with a
                // SCRUBBED message so the token can never reach a caller, an RPC error, or a log — for ANY caller
                // (connect_xmla propagates this exception to the UI; the unified opens swallow it).
                throw new InvalidOperationException($"Could not open the {kind} live connection to '{dataSource}': {ScrubSecrets(ex.Message)}");
            }
            return lc;
        }

        // Test-only: a NEVER-OPENED connection stub, so lifecycle tests can prove _live is dropped/swapped without a
        // real XMLA endpoint. Status() reports Connected (it does not probe the socket); executing a query would fail,
        // which is fine — these tests only assert connection PRESENCE and that Dispose() is clean.
        internal static LiveConnection ForTest(string kind, string dataSource, string database = null,
            Func<string, ResultSet> execute = null) =>
            new LiveConnection(kind, dataSource, new Adomd.AdomdConnection(), "", execute) { Database = database };

        public Task<ResultSet> ExecuteAsync(string query, int maxRows, int commandTimeoutSeconds) =>
            RunExclusiveAsync(() => ExecuteWithinExclusiveAsync(query, maxRows, commandTimeoutSeconds));

        /// <summary>Execute with a caller-held cancellation token: when it fires, <c>AdomdCommand.Cancel</c> is
        /// invoked (thread-safe by contract, like SqlCommand.Cancel) so the SERVER-side operation is really
        /// cancelled and the serialized query lane is released — the ADOMD call then completes with a cancellation
        /// error rather than running to the bitter end. The verify-ceiling path depends on this: an ABANDONED op
        /// would keep the lane + lifetime lease held, and abandoned ops piling up on the single lane is exactly the
        /// crash class the ceiling guards against.</summary>
        public Task<ResultSet> ExecuteAsync(string query, int maxRows, int commandTimeoutSeconds, System.Threading.CancellationToken ct) =>
            RunExclusiveAsync(() => ExecuteWithinExclusiveAsync(query, maxRows, commandTimeoutSeconds, ct));

        // DaxTrace already owns the exclusive lane while it warms and runs the captured query. Re-entering through
        // ExecuteAsync would deadlock, so trace code uses this narrow bypass. It still preserves ADOMD thread affinity.
        internal Task<ResultSet> ExecuteWithinExclusiveAsync(string query, int maxRows, int commandTimeoutSeconds,
            System.Threading.CancellationToken ct = default) =>
            _executeForTest != null
                ? Task.Run(() => _executeForTest(query))
                : _queryThread.RunAsync(() => Execute(query, maxRows <= 0 ? 10000 : maxRows, commandTimeoutSeconds, ct));

        /// <summary>ExecuteWithinExclusiveAsync PLUS the command's TRUE completion timestamp, captured
        /// SYNCHRONOUSLY on the execution-producing thread (inside the work body, immediately after the execute
        /// returns). The verify-ceiling verdict compares this against its absolute command-start deadline: an
        /// awaiter's continuation can be parked arbitrarily long (the dispatcher queues continuations
        /// asynchronously), so a timestamp taken after ANY await would mis-time an on-time completion as late —
        /// this stamp makes the continuation's resume time irrelevant in both directions.</summary>
        internal Task<(ResultSet Rs, DateTime CompletedUtc)> ExecuteWithinExclusiveStampedAsync(string query, int maxRows,
            int commandTimeoutSeconds, System.Threading.CancellationToken ct = default) =>
            _executeForTest != null
                ? Task.Run(() => { var r = _executeForTest(query); return (r, DateTime.UtcNow); })
                : _queryThread.RunAsync(() => { var r = Execute(query, maxRows <= 0 ? 10000 : maxRows, commandTimeoutSeconds, ct); return (r, DateTime.UtcNow); });

        internal async Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation)
        {
            using var lifetime = AcquireLifetimeLease();
            await _queryOperationGate.WaitAsync().ConfigureAwait(false);
            try { return await operation().ConfigureAwait(false); }
            finally { _queryOperationGate.Release(); }
        }

        internal async Task<T> RunWithLifetimeLeaseAsync<T>(Func<Task<T>> operation)
        {
            using var lifetime = AcquireLifetimeLease();
            return await operation().ConfigureAwait(false);
        }

        private ResultSet Execute(string query, int maxRows, int commandTimeoutSeconds,
            System.Threading.CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cmd = new Adomd.AdomdCommand(query, _conn) { CommandTimeout = commandTimeoutSeconds <= 0 ? 120 : commandTimeoutSeconds };
                // Real cancellation for the verify-ceiling path: the registration calls Cancel from the token's
                // callback thread (documented safe cross-thread, mirroring SqlCommand.Cancel); ExecuteReader/Read
                // then throw a cancellation error which folds into the ResultSet.Error below.
                using var reg = ct.CanBeCanceled
                    ? ct.Register(() => { try { cmd.Cancel(); } catch { /* best-effort — CommandTimeout still bounds the server */ } })
                    : default;
                using var rdr = cmd.ExecuteReader();
                var cols = Enumerable.Range(0, rdr.FieldCount)
                    .Select(i => new ColumnDef { Name = rdr.GetName(i), Type = SafeTypeName(rdr, i) })
                    .ToArray();
                var rows = new List<object[]>();
                var truncated = false;
                while (rdr.Read())
                {
                    if (rows.Count >= maxRows) { truncated = true; break; }
                    var r = new object[rdr.FieldCount];
                    for (var i = 0; i < rdr.FieldCount; i++) r[i] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                    rows.Add(r);
                }
                sw.Stop();
                return new ResultSet { Columns = cols, Rows = rows.ToArray(), RowCount = rows.Count, Truncated = truncated, ElapsedMs = sw.ElapsedMilliseconds };
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Scrub before surfacing (golden rule #1): query errors normally carry only DAX-semantic text, but
                // this Error is now also fanned out to every Studio client via model/activity, so never let a stray
                // secret (or server/RLS detail) ride a raw exception message out. ScrubSecrets is a no-op on normal text.
                // Classify auth with the NARROW, XMLA/Entra-specific matcher (this is a DAX query path, so a broad
                // matcher would flag a DAX ERROR("Unauthorized") as a sign-in problem). A typed AuthFailed marker lets
                // the interview scorer tell "sign in" from "fix the DAX" without re-sniffing the scrubbed message.
                return new ResultSet { Error = ScrubSecrets(ex.Message), AuthFailed = XmlaAuthHint.IsQueryAuthFailure(ex.Message), ElapsedMs = sw.ElapsedMilliseconds };
            }
        }

        private static string SafeTypeName(Adomd.AdomdDataReader rdr, int i)
        {
            try { return rdr.GetFieldType(i)?.Name; } catch { return null; }
        }

        // Remove any secret value (Password/PWD, but also token/bearer/access_token/key/secret) from a message before
        // it can be surfaced. Delegates to the shared XmlaAuthHint.Scrub so query errors AND connection-open errors use
        // ONE hardened scrubber (key=value in ';'/'&'/whitespace forms + bare JWTs), not two drifting copies.
        private static string ScrubSecrets(string message) => XmlaAuthHint.Scrub(message);

        public ConnectionStatus Status()
        {
            var record = ConnectionRegistry.FindByEndpoint(DataSource, Database);
            // Account is the identity THIS connection authenticated with (set on the open path) — not a registry hint,
            // which can name a stale account after a tenant-wide switch. Null reads honestly as "account unknown".
            return new ConnectionStatus { Connected = true, Kind = Kind, DataSource = DataSource, Database = Database, ConnectionId = record?.Id, Account = Account };
        }

        /// <summary>
        /// Open a fresh, authenticated AMO Server for server-side tracing (Profile / EvaluateAndLog / query plans)
        /// and cache control. Reuses the SAME connection string the live ADOMD connection authenticated with — so
        /// the bearer token rides along and tracing works on a token-auth Power BI / Fabric XMLA endpoint, not just
        /// localhost. The token stays encapsulated (never returned or logged). AMO is thread-affine: the caller must
        /// use + dispose the returned Server on its own thread.
        /// </summary>
        public TOM.Server ConnectTraceServer()
        {
            var server = new TOM.Server();
            // Join OUR query session (SessionId) so a session-scoped, session-filtered trace is permitted WITHOUT
            // server-admin — the mechanism DAX Studio uses to trace a Power BI XMLA endpoint. Falls back to a plain
            // connect (localhost / no session id), where a server trace already works.
            var cs = string.IsNullOrEmpty(SessionId) ? _connectionString : _connectionString + ";SessionId=" + SessionId;
            server.Connect(cs);
            return server;
        }

        /// <summary>
        /// Best-effort capture of THIS session's SPID from $SYSTEM.DISCOVER_SESSIONS. That DMV is row-scoped to the
        /// caller on a Power BI / Fabric XMLA endpoint (a non-server-admin sees their OWN session row), so we read all
        /// rows we're given and match our SESSION_ID, falling back to the lone row when there's no exact match (it is
        /// ours). The SPID is the universal trace-filter key (see the Spid property). Pure best-effort: any failure
        /// just leaves Spid empty and the trace falls back to a SessionID filter. Records SpidNote for diagnostics.
        /// Runs on the query thread (the connection is thread-affine) — call it there, as OpenAsync/EnsureSpid do.
        /// </summary>
        public void CaptureSpid(Adomd.AdomdConnection conn)
        {
            try
            {
                using var c = new Adomd.AdomdCommand("SELECT SESSION_SPID, SESSION_ID FROM $SYSTEM.DISCOVER_SESSIONS", conn);
                using var rdr = c.ExecuteReader();
                int spidOrd = -1, sidOrd = -1;
                for (var i = 0; i < rdr.FieldCount; i++)
                {
                    var n = rdr.GetName(i);
                    if (string.Equals(n, "SESSION_SPID", StringComparison.OrdinalIgnoreCase)) spidOrd = i;
                    else if (string.Equals(n, "SESSION_ID", StringComparison.OrdinalIgnoreCase)) sidOrd = i;
                }
                string firstSpid = null, matchSpid = null;
                var rows = 0;
                while (rdr.Read())
                {
                    rows++;
                    var sp = spidOrd >= 0 && !rdr.IsDBNull(spidOrd) ? rdr.GetValue(spidOrd)?.ToString() : null;
                    if (string.IsNullOrEmpty(firstSpid) && !string.IsNullOrEmpty(sp)) firstSpid = sp;
                    var sid = sidOrd >= 0 && !rdr.IsDBNull(sidOrd) ? rdr.GetValue(sidOrd)?.ToString() : null;
                    if (!string.IsNullOrEmpty(sid) && string.Equals(sid, SessionId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sp))
                        matchSpid = sp;
                }
                Spid = matchSpid ?? firstSpid;        // exact SESSION_ID match preferred; else the only row we got (it's ours)
                SpidNote = string.IsNullOrEmpty(Spid)
                    ? $"dmv {rows} row(s), no spid"
                    : (matchSpid != null ? "dmv match" : "dmv first-row");
            }
            catch (Exception ex)
            {
                SpidNote = "dmv err: " + ScrubSecrets(ex.Message);
            }
        }

        /// <summary>If the SPID is not yet known, retry capturing it on the query thread (the session is reliably
        /// active by the time a trace is requested, even if it wasn't at connect time). Safe to call from any thread
        /// other than the query thread.</summary>
        public void EnsureSpid()
        {
            if (!string.IsNullOrEmpty(Spid)) return;
            try { _queryThread.RunAsync(() => { CaptureSpid(_conn); return true; }).GetAwaiter().GetResult(); } catch { }
        }

        /// <summary>Persist a SPID discovered out-of-band (e.g. read off a trace event when the DMV was unavailable at
        /// open), so subsequent session traces scope by SPID and capture the SessionID-less events (query plan / eval
        /// log). Idempotent; ignores empty input.</summary>
        public void SetSpid(string spid)
        {
            if (!string.IsNullOrEmpty(spid) && string.IsNullOrEmpty(Spid)) { Spid = spid; SpidNote = (SpidNote ?? "") + "+evt"; }
        }

        // Test-observable only: the lifecycle pins assert a still-attached connection is never disposed out from
        // under _live (the SwapLive self-swap contract). Nothing in production reads it.
        internal bool DisposedForTest { get; private set; }
        internal bool HasConnectionStringForTest => !string.IsNullOrEmpty(_connectionString);

        public void Dispose()
        {
            var dispose = false;
            lock (_lifetimeGate)
            {
                _retired = true;
                dispose = _activeOperations == 0;
            }
            if (dispose) DisposeCore();
        }

        private IDisposable AcquireLifetimeLease()
        {
            lock (_lifetimeGate)
            {
                if (_retired)
                    throw new InvalidOperationException(
                        "This live connection was disconnected or replaced before the operation started. Retry against the current connection.");
                _activeOperations++;
                return new LifetimeLease(this);
            }
        }

        private void ReleaseLifetimeLease()
        {
            var dispose = false;
            lock (_lifetimeGate)
            {
                if (--_activeOperations == 0 && _retired) dispose = true;
            }
            if (dispose) DisposeCore();
        }

        private void DisposeCore()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            DisposedForTest = true;
            try { _queryThread.RunAsync(() => { try { _conn.Dispose(); } catch { } return true; }).Wait(2000); } catch { }
            _queryThread.Dispose();
            _connectionString = null;   // bearer-bearing connection strings stay transient, never retained after retirement
        }

        private sealed class LifetimeLease : IDisposable
        {
            private LiveConnection _owner;
            internal LifetimeLease(LiveConnection owner) => _owner = owner;
            public void Dispose() => System.Threading.Interlocked.Exchange(ref _owner, null)?.ReleaseLifetimeLease();
        }
    }
}
