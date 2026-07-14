using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Engine
{
    public sealed class ScanEvent
    {
        public int Line { get; set; }           // ordinal among the heaviest scans (DAX-Studio "Line")
        public string Kind { get; set; }        // VertiPaqScan / InternalScan / CacheMatch
        public string Subclass { get; set; }    // Scan / Internal / Batch (the SE query subclass)
        public long DurationMs { get; set; }
        public long CpuMs { get; set; }
        public long Rows { get; set; }
        public string Query { get; set; }        // xmSQL snippet (TextData)
    }

    /// <summary>
    /// Server-side timings for a DAX query: the Formula-Engine vs Storage-Engine split, SE query
    /// count, cache hits and the heaviest scans — captured from an Analysis Services trace while the
    /// query runs. This is what turns the wall-clock benchmark into a real "why is it slow" view.
    /// </summary>
    public sealed class ServerTimings
    {
        public long TotalMs { get; set; }
        public long FeMs { get; set; }            // Formula Engine = Total - SE
        public long SeMs { get; set; }            // Storage Engine (sum of SE query durations)
        public long SeCpuMs { get; set; }
        public int SeQueries { get; set; }
        public int SeCacheHits { get; set; }      // VertiPaqSEQueryCacheMatch count — answered from the SE data cache
        public double SeParallelism { get; set; } // SE CPU / SE duration
        public int RowCount { get; set; }
        public ScanEvent[] Scans { get; set; } = Array.Empty<ScanEvent>();
        public bool TraceAvailable { get; set; }  // false => only wall-clock TotalMs is meaningful
        public string Note { get; set; }
        public string Error { get; set; }
    }

    public sealed class EvalLogEntry
    {
        public string Label { get; set; }
        public string Expression { get; set; }    // the DAX sub-expression that was logged
        public int RowCount { get; set; }
        public string[] Columns { get; set; } = Array.Empty<string>();
        public object[][] Rows { get; set; } = Array.Empty<object[]>();
        public string Raw { get; set; }          // raw JSON payload (fallback / transparency)
    }

    /// <summary>
    /// Result of running a query that contains EVALUATEANDLOG(...) calls: the query's own result plus
    /// every logged intermediate (label, sampled rows) captured from the DAXEvaluationLog trace event.
    /// This is the "debug a measure" view — see what each step of the DAX actually evaluated to.
    /// </summary>
    public sealed class EvalLogResult
    {
        public bool TraceAvailable { get; set; }
        public EvalLogEntry[] Entries { get; set; } = Array.Empty<EvalLogEntry>();
        public ColumnDef[] ResultColumns { get; set; } = Array.Empty<ColumnDef>();
        public object[][] ResultRows { get; set; } = Array.Empty<object[]>();
        public int ResultRowCount { get; set; }
        public long ElapsedMs { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
        public string ApprovalId { get; set; }
    }

    /// <summary>One line of a DAX query plan (logical or physical), with its tree depth + #Records (physical).</summary>
    public sealed class QueryPlanNode
    {
        public int Level { get; set; }          // indent depth (leading-whitespace count) for the tree view
        public string Operator { get; set; }    // the leading operator token
        public string Detail { get; set; }      // the full (trimmed) plan line
        public long? Records { get; set; }      // #Records=N off a physical-plan line, when present
    }

    /// <summary>The logical + physical DAX query plans (the "why is it slow" structure), captured via the
    /// DAXQueryPlan trace event. The Formula Engine builds the LOGICAL plan; the Storage Engine the PHYSICAL.</summary>
    public sealed class QueryPlanResult
    {
        public bool TraceAvailable { get; set; }
        public string LogicalPlan { get; set; }
        public string PhysicalPlan { get; set; }
        public QueryPlanNode[] LogicalTree { get; set; } = Array.Empty<QueryPlanNode>();
        public QueryPlanNode[] PhysicalTree { get; set; } = Array.Empty<QueryPlanNode>();
        public long TotalMs { get; set; }
        public int RowCount { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Profiles a DAX query by running it with an AS trace attached (QueryEnd + VertiPaqSEQueryEnd +
    /// cache-match), correlating events to the live session. Each capture owns that connection's exclusive
    /// query lane from trace setup through teardown, so neither the other driver nor an ordinary query can
    /// contaminate its evidence. AMO work runs on one dedicated thread; callbacks write into locked collections.
    /// Degrades gracefully (TraceAvailable=false + wall-clock total) if the instance refuses a trace.
    /// </summary>
    public static class DaxTrace
    {
        private const string AppName = "Semanticus";

        public static Task<ServerTimings> ProfileAsync(LiveConnection live, string query)
        {
            if (live == null) return Task.FromResult(new ServerTimings { Error = "Not connected." });
            if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(new ServerTimings { Error = "Empty query." });
            return live.RunExclusiveAsync(() => Task.Run(() => Profile(live, query)));
        }

        private static ServerTimings Profile(LiveConnection live, string query)
        {
            live.EnsureSpid();                               // resolve the SPID (universal filter key) before building the filter
            var events = new List<TraceRow>();
            var spidHolder = new string[1];                  // SPID read off a trace event — fallback if the DMV never resolved it
            var gotQueryEnd = new ManualResetEventSlim(false);
            var anySeen = new ManualResetEventSlim(false);   // set on ANY trace event — confirms the subscription is live
            TOM.Server server = null;
            TOM.Trace trace = null;
            var traceUp = false;

            try
            {
                server = live.ConnectTraceServer();   // authenticated (reuses the live token) — works on cloud XMLA, not just localhost
                trace = server.Traces.Add("Semanticus-Timings-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                trace.Filter = SessionFilter(live.SessionId, live.Spid);

                AddEvent(trace, AS.TraceEventClass.QueryEnd, TimingCols);
                AddEvent(trace, AS.TraceEventClass.VertiPaqSEQueryEnd, TimingCols);
                AddEvent(trace, AS.TraceEventClass.VertiPaqSEQueryCacheMatch, MarkerCols);   // SE cache hits (no Duration/Cpu)

                trace.OnEvent += (s, e) =>
                {
                    // The server-side SessionID filter already scopes the trace to OUR query session, so every
                    // event is ours (we no longer capture ApplicationName — SE events don't support that column
                    // on the Power BI endpoint).
                    var row = new TraceRow
                    {
                        Class = e.EventClass,
                        Subclass = (int)SafeLong(() => (long)e.EventSubclass),
                        DurationMs = SafeLong(() => e.Duration),
                        CpuMs = SafeLong(() => e.CpuTime),
                        Rows = SafeLong(() => e.IntegerData),
                        Text = SafeText(e),
                    };
                    lock (events) events.Add(row);
                    anySeen.Set();
                    if (spidHolder[0] == null) { var sp = e.Spid; if (!string.IsNullOrEmpty(sp)) spidHolder[0] = sp; }
                    if (e.EventClass == AS.TraceEventClass.QueryEnd) gotQueryEnd.Set();
                };
                trace.Update();
                trace.Start();
                traceUp = true;
            }
            catch (Exception ex)
            {
                // Trace couldn't start (permissions / platform). Fall back to a plain timed run.
                Cleanup(server, trace, traceUp);
                var rs0 = live.ExecuteWithinExclusiveAsync(query, 1, 120).GetAwaiter().GetResult();
                return new ServerTimings
                {
                    TotalMs = rs0.ElapsedMs,
                    TraceAvailable = false,
                    RowCount = rs0.RowCount,
                    Note = "Server timings unavailable (no trace) — wall-clock only. " + TraceErr(ex) + " " + IdDiag(live),
                    Error = string.IsNullOrEmpty(rs0.Error) ? null : rs0.Error,
                };
            }

            // Confirm the trace is actually receiving our session's events BEFORE timing the real query — the
            // cloud subscription is async, so an immediate query loses the early SE/plan events (see WarmUpTrace).
            var warmupMs = WarmUpTrace(live, anySeen, () => { lock (events) events.Clear(); });
            gotQueryEnd.Reset();
            if (!string.IsNullOrEmpty(spidHolder[0])) live.SetSpid(spidHolder[0]);   // cache SPID for the NEXT capture's filter

            // Run the query (on the live connection's own thread) while the trace listens. Wrap in
            // try/finally so a fault during the run still tears down the Server + Trace AND the server-side
            // trace registration we started — otherwise it lingers on the AS instance.
            var sw = Stopwatch.StartNew();
            ResultSet rs;
            try
            {
                rs = live.ExecuteWithinExclusiveAsync(query, 1, 120).GetAwaiter().GetResult();
                sw.Stop();
                gotQueryEnd.Wait(TimeSpan.FromSeconds(6)); // let trailing trace events (esp. QueryEnd) flush
                Thread.Sleep(150);
            }
            finally { Cleanup(server, trace, traceUp); }

            if (!string.IsNullOrEmpty(rs.Error))
                return new ServerTimings { Error = rs.Error, TraceAvailable = true };

            List<TraceRow> snap;
            lock (events) snap = events.ToList();

            var qe = snap.Where(r => r.Class == AS.TraceEventClass.QueryEnd).OrderByDescending(r => r.DurationMs).FirstOrDefault();
            var se = snap.Where(r => r.Class == AS.TraceEventClass.VertiPaqSEQueryEnd).ToList();
            var cacheHits = snap.Count(r => r.Class == AS.TraceEventClass.VertiPaqSEQueryCacheMatch);

            var total = qe?.DurationMs ?? sw.ElapsedMilliseconds;
            var seMs = Math.Min(se.Sum(r => r.DurationMs), total); // sub-scans can sum past total; cap for sanity
            var seCpu = se.Sum(r => r.CpuMs);

            // No SE rows => either a genuine no-scan answer (cache/metadata) or the trace captured nothing. Attach
            // diagnostics (warm-up result + raw event count) so the cause is visible during the cloud bring-up.
            var note = se.Count > 0 ? null
                : (qe != null ? "Answered with no storage-engine scan (cached or metadata-only) — Formula-Engine time only. " : "")
                  + TraceDiag(live, warmupMs, snap.Count, $"qe={(qe != null ? 1 : 0)} se=0 cache={cacheHits}");

            return new ServerTimings
            {
                TotalMs = total,
                SeMs = seMs,
                SeCpuMs = seCpu,
                FeMs = Math.Max(0, total - seMs),
                SeQueries = se.Count,
                SeCacheHits = cacheHits,
                SeParallelism = seMs > 0 ? Math.Round((double)seCpu / seMs, 2) : 0,
                RowCount = rs.RowCount,
                TraceAvailable = qe != null || se.Count > 0,
                Scans = se.OrderByDescending(r => r.DurationMs).Take(15)
                    .Select((r, i) => new ScanEvent { Line = i + 1, Kind = "VertiPaqScan", Subclass = SubclassLabel(r.Subclass), DurationMs = r.DurationMs, CpuMs = r.CpuMs, Rows = r.Rows, Query = Snippet(r.Text) })
                    .ToArray(),
                Note = note,
            };
        }

        // Column validity is per-event and only checked at trace.Update() (server-side), so the column
        // sets must be correct up front. Timing events carry Duration/CpuTime/rows; the cache-match
        // marker event supports neither.
        // No ApplicationName: storage-engine events (VertiPaqSEQueryEnd/CacheMatch) don't support it on the Power
        // BI XMLA endpoint. The session-id trace filter scopes events to our query instead. Mirrors sempy's schema.
        private static readonly AS.TraceColumn[] TimingCols =
        {
            AS.TraceColumn.EventClass, AS.TraceColumn.EventSubclass, AS.TraceColumn.Duration,
            AS.TraceColumn.CpuTime, AS.TraceColumn.IntegerData, AS.TraceColumn.TextData,
            AS.TraceColumn.SessionID,   // present so the session-id trace filter has a column to match (sempy schema)
            AS.TraceColumn.Spid,        // QueryEnd + VertiPaqSEQueryEnd both carry SPID (id 41) — read it to learn our SPID
        };
        // The SPID column is REQUIRED on every event we want the SPID trace-filter to match: a session/SPID filter
        // only matches an event class that actually COLLECTS the filtered column. DAXQueryPlan (112),
        // DAXEvaluationLog (135) and VertiPaqSEQueryCacheMatch (85) all accept SPID (id 41) — verified directly
        // against a live engine — so without it here the SPID filter silently dropped the query plan + EVALUATEANDLOG
        // events (only the SPID-bearing QueryEnd survived). It also lets us read our own SPID off any of these events.
        private static readonly AS.TraceColumn[] MarkerCols =
        {
            AS.TraceColumn.EventClass, AS.TraceColumn.EventSubclass, AS.TraceColumn.TextData,
            AS.TraceColumn.SessionID, AS.TraceColumn.Spid,
        };

        // Scope the trace to OUR session (and our ApplicationName, "Semanticus", as a fallback match) — this is
        // what lets a non-server-admin trace a Power BI XMLA endpoint (you may trace your own session). Mirrors
        // DAX Studio's session filter. Built via the DOM so values are escaped and namespaced correctly.
        private static System.Xml.XmlNode SessionFilter(string sessionId, string spid)
        {
            const string ns = "http://schemas.microsoft.com/analysisservices/2003/engine";
            var doc = new System.Xml.XmlDocument();
            System.Xml.XmlElement Eq(AS.TraceColumn col, string val)
            {
                var e = doc.CreateElement("Equal", ns);
                var c = doc.CreateElement("ColumnID", ns); c.InnerText = ((int)col).ToString(); e.AppendChild(c);
                var v = doc.CreateElement("Value", ns); v.InnerText = val ?? ""; e.AppendChild(v);
                return e;
            }
            // Scope the trace to OUR query session with a SINGLE equality (an <Or> wrapper coincided with capture
            // dropping out entirely). Prefer SPID: per the AS trace schema EVERY event we collect — QueryEnd,
            // VertiPaqSEQueryEnd/CacheMatch (ids 83/85) and DAXQueryPlan — carries the SPID column (id 41, the
            // server process id that "directly corresponds to the XMLA session GUID"), and SPID is a *derived*
            // filter column so it needs no collected column. SessionID (id 39) is the sempy-proven fallback when
            // the SPID DMV is unavailable; ApplicationName is a localhost-only last resort (the Power BI endpoint
            // rejects it on SE events — "event 85 does not contain column 37").
            System.Xml.XmlElement root =
                !string.IsNullOrEmpty(spid)      ? Eq(AS.TraceColumn.Spid, spid)
              : !string.IsNullOrEmpty(sessionId) ? Eq(AS.TraceColumn.SessionID, sessionId)
              :                                    Eq(AS.TraceColumn.ApplicationName, AppName);
            doc.AppendChild(root);
            return root;
        }

        // Surface the REAL server error (scrubbed) behind a trace failure so it's diagnosable instead of an
        // opaque "(OperationException)". Trace operation errors carry server permission/session detail, not the token.
        private static string TraceErr(Exception ex)
        {
            var msg = System.Text.RegularExpressions.Regex.Replace(ex.Message ?? "", @"(?i)\b(password|pwd)\s*=\s*[^;]*", "$1=***").Trim();
            return ex.GetType().Name + (string.IsNullOrEmpty(msg) ? "" : ": " + msg);
        }

        // A cloud XMLA trace subscribes to events ASYNCHRONOUSLY after Start() — Microsoft documents this for
        // sempy.fabric.Trace ("after starting the trace there may be a slight delay as the engine registers and
        // subscribes; if no events are logged, increase the delay" — events are best-effort, server-load dependent).
        // Firing the real query before the subscription is live silently drops the EARLY events (the SE scans and
        // the query plan happen first; only the trailing QueryEnd sometimes survives — exactly the "wall-clock only /
        // no SE / no plan" symptom). So we warm up: run a trivial ping query and wait until it surfaces through the
        // trace, re-pinging until it does. Returns ms-to-live, or -1 on timeout (=> the filter isn't matching our
        // session). On success the ping's own captured events are cleared so they don't pollute the real capture.
        private static long WarmUpTrace(LiveConnection live, ManualResetEventSlim anySeen, Action clearCaptured)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(8))
            {
                try { live.ExecuteWithinExclusiveAsync("EVALUATE { 1 }", 1, 30).GetAwaiter().GetResult(); } catch { }
                if (anySeen.Wait(TimeSpan.FromMilliseconds(1500)))
                {
                    sw.Stop();
                    Thread.Sleep(300);    // let the ping's trailing async events (QueryEnd / physical plan) flush first
                    clearCaptured();      // then drop them ALL so only the real query is reported
                    anySeen.Reset();
                    return sw.ElapsedMilliseconds;
                }
            }
            sw.Stop();
            clearCaptured();
            anySeen.Reset();
            return -1;
        }

        // Compact, secret-free diagnostics surfaced in the Note while we bring server-side tracing up on cloud XMLA.
        // SPID is a process id (not a secret); SessionID is reported only as present/absent. Removable once stable.
        private static string SpidDiag(LiveConnection live) =>
            string.IsNullOrEmpty(live.Spid)
                ? "∅" + (string.IsNullOrEmpty(live.SpidNote) ? "" : "(" + live.SpidNote + ")")
                : live.Spid;

        private static string TraceDiag(LiveConnection live, long warmupMs, int rawEvents, string byClass) =>
            $"[trace: sid={(string.IsNullOrEmpty(live.SessionId) ? "∅" : "set")} spid={SpidDiag(live)} " +
            $"warmup={(warmupMs < 0 ? "TIMEOUT" : warmupMs + "ms")} raw={rawEvents}{(string.IsNullOrEmpty(byClass) ? "" : " " + byClass)}]";

        private static string IdDiag(LiveConnection live) =>
            $"[sid={(string.IsNullOrEmpty(live.SessionId) ? "∅" : "set")} spid={SpidDiag(live)}]";

        public static Task<EvalLogResult> EvaluateAndLogAsync(LiveConnection live, string query, int maxRows)
        {
            if (live == null) return Task.FromResult(new EvalLogResult { Error = "Not connected." });
            if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(new EvalLogResult { Error = "Empty query." });
            return live.RunExclusiveAsync(() => Task.Run(() => EvaluateAndLog(live, query, maxRows <= 0 ? 1000 : maxRows)));
        }

        private static EvalLogResult EvaluateAndLog(LiveConnection live, string query, int maxRows)
        {
            live.EnsureSpid();                               // resolve the SPID (universal filter key) before building the filter
            var logs = new List<string>();
            var raw = new int[1];                            // total events seen (any class) — for diagnostics
            var spidHolder = new string[1];                  // SPID read off a trace event — fallback if the DMV never resolved it
            var gotQueryEnd = new ManualResetEventSlim(false);
            var anySeen = new ManualResetEventSlim(false);   // set on ANY trace event — confirms the subscription is live
            TOM.Server server = null;
            TOM.Trace trace = null;
            var traceUp = false;

            try
            {
                server = live.ConnectTraceServer();   // authenticated (reuses the live token) — works on cloud XMLA, not just localhost
                trace = server.Traces.Add("Semanticus-EvalLog-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                trace.Filter = SessionFilter(live.SessionId, live.Spid);
                AddEvent(trace, AS.TraceEventClass.DAXEvaluationLog, EvalLogCols);
                AddEvent(trace, AS.TraceEventClass.QueryEnd, MarkerCols);
                trace.OnEvent += (s, e) =>
                {
                    System.Threading.Interlocked.Increment(ref raw[0]);
                    anySeen.Set();
                    if (spidHolder[0] == null) { var sp = e.Spid; if (!string.IsNullOrEmpty(sp)) spidHolder[0] = sp; }
                    if (e.EventClass == AS.TraceEventClass.DAXEvaluationLog)
                    {
                        var txt = SafeText(e);
                        if (!string.IsNullOrEmpty(txt)) lock (logs) logs.Add(txt);
                    }
                    else if (e.EventClass == AS.TraceEventClass.QueryEnd)
                    {
                        // session-filtered to our query, so any QueryEnd is ours
                        gotQueryEnd.Set();
                    }
                };
                trace.Update();
                trace.Start();
                traceUp = true;
            }
            catch (Exception ex)
            {
                Cleanup(server, trace, traceUp);
                var rsf = live.ExecuteWithinExclusiveAsync(query, maxRows, 120).GetAwaiter().GetResult();
                return new EvalLogResult
                {
                    TraceAvailable = false,
                    ResultColumns = rsf.Columns,
                    ResultRows = rsf.Rows,
                    ResultRowCount = rsf.RowCount,
                    ElapsedMs = rsf.ElapsedMs,
                    Error = string.IsNullOrEmpty(rsf.Error) ? null : rsf.Error,
                    Note = "Trace unavailable — ran the query but could not capture EVALUATEANDLOG output. " + TraceErr(ex) + " " + IdDiag(live),
                };
            }

            // Confirm the subscription is live before running the real query (see WarmUpTrace), then drop the ping's events.
            var warmupMs = WarmUpTrace(live, anySeen, () => { lock (logs) logs.Clear(); System.Threading.Interlocked.Exchange(ref raw[0], 0); });
            gotQueryEnd.Reset();
            if (!string.IsNullOrEmpty(spidHolder[0])) live.SetSpid(spidHolder[0]);   // cache SPID for the NEXT capture's filter

            ResultSet rs;
            try
            {
                rs = live.ExecuteWithinExclusiveAsync(query, maxRows, 180).GetAwaiter().GetResult();
                gotQueryEnd.Wait(TimeSpan.FromSeconds(6));
                Thread.Sleep(200);
            }
            finally { Cleanup(server, trace, traceUp); }

            List<string> snap;
            lock (logs) snap = logs.ToList();
            var entries = snap.Select(ParseEvalLog).Where(e => e != null).ToArray();

            return new EvalLogResult
            {
                TraceAvailable = true,
                Entries = entries,
                ResultColumns = rs.Columns,
                ResultRows = rs.Rows,
                ResultRowCount = rs.RowCount,
                ElapsedMs = rs.ElapsedMs,
                Error = string.IsNullOrEmpty(rs.Error) ? null : rs.Error,
                Note = entries.Length == 0
                    ? "Query ran but no EVALUATEANDLOG output was captured — add EVALUATEANDLOG(<expr>, \"label\") around the sub-expressions you want to inspect. "
                      + TraceDiag(live, warmupMs, raw[0], $"log={snap.Count}")
                    : null,
            };
        }

        // Parses the DAXEvaluationLog payload:
        //   { "expression": "...", "label": "...", "inputs": [...],
        //     "data": [ { "input": [v0,v1,...], "output": <value> }, ... ] }
        // Presented as a table: columns = input column names (+ "output"); rows = each data entry.
        // Defensive: unknown shapes keep Raw so nothing is lost.
        private static EvalLogEntry ParseEvalLog(string json)
        {
            var entry = new EvalLogEntry { Raw = json };
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(json);
                entry.Label = (string)(o["label"] ?? o["name"]);
                entry.Expression = (string)o["expression"];

                var inputNames = (o["inputs"] as Newtonsoft.Json.Linq.JArray)?
                    .Select(t => t.Type == Newtonsoft.Json.Linq.JTokenType.Object ? (string)(t["name"] ?? t["Name"]) : (string)t)
                    .Where(s => s != null).ToList() ?? new List<string>();

                var rows = new List<object[]>();
                if (o["data"] is Newtonsoft.Json.Linq.JArray data)
                {
                    foreach (var d in data)
                    {
                        var cells = new List<object>();
                        if (d["input"] is Newtonsoft.Json.Linq.JArray inArr)
                            foreach (var c in inArr) cells.Add(JVal(c));
                        cells.Add(JVal(d["output"]));
                        rows.Add(cells.ToArray());
                    }
                }

                var inputWidth = rows.Count > 0 ? Math.Max(0, rows.Max(r => r.Length) - 1) : inputNames.Count;
                var cols = new List<string>();
                for (var i = 0; i < inputWidth; i++) cols.Add(i < inputNames.Count ? inputNames[i] : "input" + (i + 1));
                cols.Add("output");

                entry.Columns = cols.ToArray();
                entry.Rows = rows.ToArray();
                entry.RowCount = rows.Count;
            }
            catch { /* keep Raw only */ }
            return entry;
        }

        private static object JVal(Newtonsoft.Json.Linq.JToken t)
        {
            if (t == null || t.Type == Newtonsoft.Json.Linq.JTokenType.Null) return null;
            switch (t.Type)
            {
                case Newtonsoft.Json.Linq.JTokenType.Integer:
                case Newtonsoft.Json.Linq.JTokenType.Float:
                case Newtonsoft.Json.Linq.JTokenType.Boolean: return ((Newtonsoft.Json.Linq.JValue)t).Value;
                case Newtonsoft.Json.Linq.JTokenType.String:
                case Newtonsoft.Json.Linq.JTokenType.Date: return t.ToString();
                default: return t.ToString(Newtonsoft.Json.Formatting.None); // nested table/object
            }
        }

        // DAXEvaluationLog (event 135) is picky: it has no EventSubclass — just the JSON in TextData. It DOES carry
        // SPID (verified against a live engine) — required so the SPID trace-filter actually matches it.
        private static readonly AS.TraceColumn[] EvalLogCols =
        {
            AS.TraceColumn.EventClass, AS.TraceColumn.TextData, AS.TraceColumn.SessionID, AS.TraceColumn.Spid,
        };

        private static void AddEvent(TOM.Trace trace, AS.TraceEventClass cls, AS.TraceColumn[] cols)
        {
            var ev = trace.Events.Add(cls);
            foreach (var col in cols) ev.Columns.Add(col);
        }

        private static void Cleanup(TOM.Server server, TOM.Trace trace, bool traceUp)
        {
            try { if (trace != null) { if (traceUp && trace.IsStarted) trace.Stop(); if (trace.Parent != null) trace.Drop(); trace.Dispose(); } } catch { }
            try { if (server != null) { server.Disconnect(); server.Dispose(); } } catch { }
        }

        // ---- Query plans (logical + physical) — the DAXQueryPlan trace event (id 112) -----------------------
        public static Task<QueryPlanResult> CaptureQueryPlanAsync(LiveConnection live, string query)
        {
            if (live == null) return Task.FromResult(new QueryPlanResult { Error = "Not connected." });
            if (string.IsNullOrWhiteSpace(query)) return Task.FromResult(new QueryPlanResult { Error = "Empty query." });
            return live.RunExclusiveAsync(() => Task.Run(() => CapturePlan(live, query)));
        }

        private static QueryPlanResult CapturePlan(LiveConnection live, string query)
        {
            const int PlanCap = 20000;
            live.EnsureSpid();                               // resolve the SPID (universal filter key) before building the filter
            var logical = new System.Text.StringBuilder();
            var physical = new System.Text.StringBuilder();
            var raw = new int[1];                            // total events seen (any class) — for diagnostics
            var spidHolder = new string[1];                  // SPID read off a trace event — fallback if the DMV never resolved it
            var gotQueryEnd = new ManualResetEventSlim(false);
            var anySeen = new ManualResetEventSlim(false);   // set on ANY trace event — confirms the subscription is live
            TOM.Server server = null; TOM.Trace trace = null; var traceUp = false;
            try
            {
                server = live.ConnectTraceServer();   // authenticated (reuses the live token) — works on cloud XMLA, not just localhost
                trace = server.Traces.Add("Semanticus-Plan-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                trace.Filter = SessionFilter(live.SessionId, live.Spid);
                AddEvent(trace, AS.TraceEventClass.DAXQueryPlan, MarkerCols);
                AddEvent(trace, AS.TraceEventClass.QueryEnd, MarkerCols);
                trace.OnEvent += (s, e) =>
                {
                    System.Threading.Interlocked.Increment(ref raw[0]);
                    anySeen.Set();
                    if (spidHolder[0] == null) { var sp = e.Spid; if (!string.IsNullOrEmpty(sp)) spidHolder[0] = sp; }
                    if (e.EventClass == AS.TraceEventClass.QueryEnd) { gotQueryEnd.Set(); return; }
                    if (e.EventClass != AS.TraceEventClass.DAXQueryPlan) return;
                    var txt = SafeText(e);
                    if (string.IsNullOrEmpty(txt)) return;
                    // Route by the subclass NAME, not a magic int: the real values are DAXVertiPaqLogicalPlan=216 /
                    // DAXVertiPaqPhysicalPlan=217 (+ DAXDirectQuery* variants) — the old 1/2 guess matched neither, so
                    // BOTH plans fell into the logical bucket and Physical was always empty. Verified against a live engine.
                    string subName = null; try { subName = e.EventSubclass.ToString(); } catch { }
                    var isPhysical = subName != null && subName.IndexOf("Physical", StringComparison.OrdinalIgnoreCase) >= 0;
                    var sb = isPhysical ? physical : logical;
                    lock (sb) { if (sb.Length < PlanCap) sb.AppendLine(txt); }
                };
                trace.Update(); trace.Start(); traceUp = true;
            }
            catch (Exception ex)
            {
                Cleanup(server, trace, traceUp);
                var rs0 = live.ExecuteWithinExclusiveAsync(query, 1, 120).GetAwaiter().GetResult();
                return new QueryPlanResult
                {
                    TraceAvailable = false, TotalMs = rs0.ElapsedMs, RowCount = rs0.RowCount,
                    Note = "Query plans unavailable (no trace). " + TraceErr(ex) + " " + IdDiag(live),
                    Error = string.IsNullOrEmpty(rs0.Error) ? null : rs0.Error,
                };
            }

            // Confirm the subscription is live before running the real query (the plan events fire FIRST, so an
            // un-warmed trace misses them entirely — see WarmUpTrace), then drop the ping's own plan.
            var warmupMs = WarmUpTrace(live, anySeen, () => { lock (logical) logical.Clear(); lock (physical) physical.Clear(); System.Threading.Interlocked.Exchange(ref raw[0], 0); });
            gotQueryEnd.Reset();
            if (!string.IsNullOrEmpty(spidHolder[0])) live.SetSpid(spidHolder[0]);   // cache SPID for the NEXT capture's filter

            var sw = Stopwatch.StartNew();
            ResultSet rs;
            try { rs = live.ExecuteWithinExclusiveAsync(query, 1, 120).GetAwaiter().GetResult(); sw.Stop(); gotQueryEnd.Wait(TimeSpan.FromSeconds(6)); Thread.Sleep(150); }
            finally { Cleanup(server, trace, traceUp); }

            if (!string.IsNullOrEmpty(rs.Error)) return new QueryPlanResult { Error = rs.Error, TraceAvailable = true };

            string lp, pp;
            lock (logical) lp = logical.ToString().TrimEnd();
            lock (physical) pp = physical.ToString().TrimEnd();
            var available = lp.Length > 0 || pp.Length > 0;
            return new QueryPlanResult
            {
                TraceAvailable = available,
                LogicalPlan = lp, PhysicalPlan = pp,
                LogicalTree = ParsePlan(lp), PhysicalTree = ParsePlan(pp),
                TotalMs = sw.ElapsedMilliseconds, RowCount = rs.RowCount,
                Note = available ? null
                    : "No query plan captured (the engine may have answered from cache / metadata only). " + TraceDiag(live, warmupMs, raw[0], null),
            };
        }

        // Parse a plan's indented text into nodes: depth = leading-whitespace count; operator = the leading token;
        // #Records=N pulled off physical lines. Capped to keep the broadcast/UI payload bounded.
        private static QueryPlanNode[] ParsePlan(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<QueryPlanNode>();
            var nodes = new List<QueryPlanNode>();
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (nodes.Count >= 500) break;
                var trimmed = raw.TrimStart();
                var level = raw.Length - trimmed.Length;
                long? records = null;
                var m = System.Text.RegularExpressions.Regex.Match(trimmed, @"#Records=(\d+)");
                if (m.Success && long.TryParse(m.Groups[1].Value, out var rec)) records = rec;
                var op = trimmed.Split(':')[0].Split('<')[0].Trim();
                nodes.Add(new QueryPlanNode
                {
                    Level = level,
                    Operator = op.Length > 80 ? op.Substring(0, 80) : op,
                    Detail = trimmed.Length > 300 ? trimmed.Substring(0, 300) + "…" : trimmed,
                    Records = records,
                });
            }
            return nodes.ToArray();
        }

        private static long SafeLong(Func<long> f) { try { return f(); } catch { return 0; } }
        private static string SafeText(TOM.TraceEventArgs e) { try { return e.TextData; } catch { return null; } }
        private static string Snippet(string s) => string.IsNullOrEmpty(s) ? null : (s.Length > 400 ? s.Substring(0, 400) + "…" : s);

        private sealed class TraceRow
        {
            public AS.TraceEventClass Class;
            public int Subclass;
            public long DurationMs;
            public long CpuMs;
            public long Rows;
            public string Text;
        }

        private static string SubclassLabel(int sc) => sc switch { 0 => "Scan", 10 => "Internal", 11 => "Batch", _ => sc.ToString() };
    }
}
