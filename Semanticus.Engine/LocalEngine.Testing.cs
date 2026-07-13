using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// The Tests-tab reconciliation RUNNER (the impure half of MeasureReconciler). It owns the two connections, the
    /// join, and the coercion the pure judge cannot see (docs/tests-tab-runner-contract.md): it runs the DAX side over
    /// the live XMLA connection and the caller's INDEPENDENT ground-truth SQL over TDS, joins them by the composite
    /// grouping key, coerces provider cells to the reconciler's per-side vocabulary, and hands the assembled cells to
    /// <see cref="MeasureReconciler.Reconcile"/> for the verdict. The pure pieces (join / key discipline / coercion /
    /// duplicate detection) live in <see cref="ReconcileJoiner"/> / <see cref="ReconcileKeyEncoder"/> /
    /// <see cref="ReconcileCoercion"/> so they are unit-testable OFFLINE.
    ///
    /// No model mutation, no undo entry, no broadcast — it only reads data (like run_dax / preview_table). It reads
    /// source-row aggregates by group from BOTH the model (XMLA/DAX) and the source SQL endpoint, as the user's OWN
    /// identity. Governance for an AGENT is the STANDARD QueryData permission gate (the permissions tab) on each target
    /// — the same gate preview_table / run_dax use; a human runs ungated.
    /// </summary>
    public sealed partial class LocalEngine
    {
        // The DAX value + retention-sentinel column names (never collide with a real grouping column).
        private const string ReconValueCol = "__recon_value";
        private const string ReconPresentCol = "__recon_present";

        public async Task<ReconcileMappingReview> ReviewReconcileMappingAsync(ReconcileMappingRequest req, string origin = "human")
        {
            if (req == null) return new ReconcileMappingReview { Error = "No mapping request supplied." };
            var s = _sessions.Current;
            if (s == null) return new ReconcileMappingReview { Error = "No open model. Use open_model, connect_local or connect_xmla first." };

            var review = await s.ReadAsync(m => BuildReconcileMappingReview(m, req.MeasureRef));
            if (review.Error != null) return review;
            review.EffectiveServer = string.IsNullOrWhiteSpace(req.Server) ? review.DetectedServer : req.Server.Trim();
            review.EffectiveDatabase = string.IsNullOrWhiteSpace(req.Database) ? review.DetectedDatabase : req.Database.Trim();
            if (!req.TestConnection) return review;

            review.Tested = true;
            if (string.IsNullOrWhiteSpace(review.EffectiveServer) || string.IsNullOrWhiteSpace(review.EffectiveDatabase))
            {
                review.TestError = "Enter a SQL endpoint and database before testing the connection.";
                review.SuggestedNextAction = "supply server + database, then test again";
                return review;
            }

            var gate = GuardAgent(AgentCapability.QueryData, review.EffectiveServer, review.EffectiveDatabase, origin, isCommit: true,
                summary: $"test the reconciliation SQL connection for {review.MeasureName}: {review.EffectiveDatabase} on {review.EffectiveServer}",
                approvalId: out var approvalId, intentBasis: "querydata", consumeGrant: false);
            if (gate != null)
            {
                review.TestError = gate;
                review.ApprovalId = approvalId;
                review.SuggestedNextAction = "approve the pending QueryData request in Permissions, then test again";
                return review;
            }

            string token;
            try { token = await EntraToken.AcquireSqlAsync(req.AuthMode, null, CancellationToken.None, req.TenantId).ConfigureAwait(false); }
            catch (Exception ex)
            {
                review.TestError = "Could not acquire a SQL token: " + ScrubSchemaError(ex.Message);
                review.SuggestedNextAction = "fix SQL authentication, then test again";
                return review;
            }

            var rs = await FabricSqlQuery.ExecuteAsync(review.EffectiveServer, review.EffectiveDatabase, token,
                "SELECT CAST(1 AS bigint) AS [semanticus_connection_test]", 1, 30, CancellationToken.None).ConfigureAwait(false);
            review.ElapsedMs = rs.ElapsedMs;
            if (!string.IsNullOrEmpty(rs.Error))
            {
                review.TestError = "Connection test failed: " + rs.Error;
                review.SuggestedNextAction = "check the endpoint, database and identity access, then test again";
                return review;
            }
            review.Connected = rs.Rows.Length == 1 && rs.Columns.Length > 0;
            if (!review.Connected)
            {
                review.TestError = $"Connection test returned an unexpected shape ({rs.Rows.Length} row(s), {rs.Columns.Length} column(s)).";
                review.SuggestedNextAction = "test again; if this repeats, inspect the SQL endpoint";
            }
            return review;
        }

        public async Task<ReconcileRunResult> ReconcileMeasureAsync(ReconcileRequest req, string origin = "human")
        {
            if (req == null)
                return ReconcileRunResult.Fail("No request supplied.", ReconcileStatus.InputError);

            // (contract d/e) The blank policy is a REQUIRED, deliberate choice — parse it before spending a run, and
            // teach the three options rather than defaulting BLANK/NULL/0 semantics silently.
            if (!TryParseBlankPolicy(req.BlankPolicy, out var blank))
                return ReconcileRunResult.Fail(
                    $"A blank policy is required: pass blankPolicy = \"zero\" (BLANK/NULL read as 0; additive measures), "
                    + "\"null\" (BLANK≈NULL, both differ from 0; averages/ratios), or \"distinct\" (BLANK, NULL and 0 all "
                    + "distinct; the strictest). No default is applied so the BLANK/NULL/0 reading is a choice, not a guess.",
                    ReconcileStatus.InputError);

            // (contract d) Validate the tolerance at the boundary so the user gets the message before a run is spent
            // (the reconciler would also reject it, but only after the queries have run).
            if (!IsFinite(req.ToleranceAbsolute) || req.ToleranceAbsolute < 0 || !IsFinite(req.ToleranceRelative) || req.ToleranceRelative < 0)
                return ReconcileRunResult.Fail(
                    $"Invalid tolerance (absolute={Inv(req.ToleranceAbsolute)}, relative={Inv(req.ToleranceRelative)}). Both must be "
                    + "finite and >= 0; relative is a fraction of abs(sql), so 1.0 = 100%, not 1%. TolerancePolicy.Default is 1e-7 / 1e-9.",
                    ReconcileStatus.InputError);

            var policy = new TolerancePolicy { Absolute = req.ToleranceAbsolute, Relative = req.ToleranceRelative, Blank = blank };
            var groupBy = (req.GroupBy ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToArray();
            var grouped = groupBy.Length > 0;

            if (string.IsNullOrWhiteSpace(req.Sql))
                return ReconcileRunResult.Fail(
                    "No ground-truth SQL supplied. Author the SQL that computes the same number independently (the engine "
                    + "never writes it); in grouped mode it returns the grouping key columns then the aggregate value.",
                    ReconcileStatus.InputError);

            // Resolve the measure, RESOLVE each grouping entry against the model's real columns (P1-3 — raw text is
            // NEVER spliced into DAX; only engine-constructed refs for resolved objects are emitted), and derive the
            // SQL coordinates in ONE model read (pure, no mutation). Done BEFORE the live check so a bad ref / injection
            // attempt is a clean InputError, not masked by "not connected".
            var s = _sessions.Require();
            var probe = await s.ReadAsync(m => ResolveMeasureAndSource(m, req.MeasureRef, groupBy));
            if (probe.Error != null)
                return ReconcileRunResult.Fail(probe.Error, ReconcileStatus.InputError, next: "list_measures / list_columns");

            var server = string.IsNullOrWhiteSpace(req.Server) ? probe.Server : req.Server.Trim();
            var database = string.IsNullOrWhiteSpace(req.Database) ? probe.Database : req.Database.Trim();
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
                return ReconcileRunResult.Fail(
                    "No Fabric SQL endpoint to run the ground-truth SQL against. Pass server + database, or open a model "
                    + "whose tables have a Sql.Database(...) source the engine can derive them from.",
                    ReconcileStatus.InputError, next: "pass server + database");

            // SECURITY = the STANDARD QueryData permission gate (the permissions tab), same as preview_table / run_dax.
            // The reconcile reads source-row aggregates as the USER'S OWN identity; whether an AGENT may do so is
            // governed by the QueryData setting on each target (Ask/Deny/Allow). Humans are never gated. We gate BOTH
            // resources the run reads: (1) the SQL endpoint the ground truth is queried against, (2) the XMLA model
            // (the engine-authored DAX). One "querydata" session grant per target (consumeGrant:false), like the family.
            //
            // The SQL-target gate runs BEFORE the connection check: it needs only the caller-supplied server/database,
            // so a denied agent should learn "denied" (fail-fast, honest order) BEFORE "not connected", and the gate is
            // exercisable without a live endpoint. The XMLA gate dereferences the live connection, so it necessarily
            // stays after the connection check.
            var sqlGate = GuardAgent(AgentCapability.QueryData, server, database, origin, isCommit: true,
                summary: $"reconcile measure {probe.MeasureName}: read ground-truth rows from {database} on {server}",
                approvalId: out var sqlApprovalId, intentBasis: "querydata", consumeGrant: false);
            if (sqlGate != null)
                return ReconcileRunResult.Fail(sqlGate, ReconcileStatus.InputError, refused: true, approvalId: sqlApprovalId);

            var live = _live;
            if (live == null)
                return ReconcileRunResult.Fail("Not connected. Call connect_xmla or connect_local first, then re-run.",
                    ReconcileStatus.InsufficientCoverage, next: "connect_xmla or connect_local");

            var xmlaGate = GuardAgent(AgentCapability.QueryData, live.DataSource, live.Database, origin, isCommit: true,
                summary: $"reconcile measure {probe.MeasureName}: read model rows from {(string.IsNullOrEmpty(live.Database) ? live.DataSource : live.Database + " on " + live.DataSource)}",
                approvalId: out var xmlaApprovalId, intentBasis: "querydata", consumeGrant: false);
            if (xmlaGate != null)
                return ReconcileRunResult.Fail(xmlaGate, ReconcileStatus.InputError, refused: true, approvalId: xmlaApprovalId);

            string token;
            try { token = await EntraToken.AcquireSqlAsync(req.AuthMode, null, CancellationToken.None, req.TenantId).ConfigureAwait(false); }
            catch (Exception ex) { return ReconcileRunResult.Fail("Could not acquire a SQL token: " + ScrubSchemaError(ex.Message), ReconcileStatus.InsufficientCoverage); }

            var maxRows = req.MaxRows <= 0 ? 100000 : req.MaxRows;
            var cells = new List<ReconcileCell>();
            var snapshotNote = "DAX and SQL run on independent connections and may observe different source snapshots; "
                + "execution timestamps are recorded but snapshot isolation is NOT guaranteed.";
            var result = new ReconcileRunResult
            {
                ToleranceAbsolute = req.ToleranceAbsolute,
                ToleranceRelative = req.ToleranceRelative,
                BlankPolicy = blank.ToString(),
                Complete = true,
                // (contract f) We cannot enumerate the universe of expected members a-priori, so coverage of expected
                // members is UNKNOWN — a green means "reconciled over the members observed", never "all members checked".
                CoverageKnown = false,
                SnapshotNote = snapshotNote,
            };
            bool truncated = false;

            // ---- member cells (grouped mode) ----
            if (grouped)
            {
                var memberQuery = BuildDaxMemberQuery(probe.MeasureName, probe.GroupByRefs);
                result.DaxQuery = string.IsNullOrWhiteSpace(req.SqlGrandTotal)
                    ? memberQuery
                    : memberQuery + "\n\n// Grand total, evaluated independently\n" + BuildDaxGrandTotalQuery(probe.MeasureName);
                result.DaxExecutedAtUtc = DateTime.UtcNow;
                var daxRs = await live.ExecuteAsync(memberQuery, maxRows, 120).ConfigureAwait(false);
                if (daxRs.PolicyRefused) return ReconcileRunResult.Fail(daxRs.Error, ReconcileStatus.InputError, refused: true);
                if (!string.IsNullOrEmpty(daxRs.Error)) return QueryFail("DAX member query", daxRs.Error, result.DaxQuery);
                result.DaxElapsedMs = daxRs.ElapsedMs;

                result.SqlExecutedAtUtc = DateTime.UtcNow;
                var sqlRs = await FabricSqlQuery.ExecuteAsync(server, database, token, req.Sql, maxRows, 120, CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(sqlRs.Error)) return QueryFail("SQL member query", sqlRs.Error, result.DaxQuery);
                result.SqlElapsedMs = sqlRs.ElapsedMs;
                if (sqlRs.Columns.Length < groupBy.Length + 1)
                    return ReconcileRunResult.Fail(
                        $"The ground-truth SQL returned {sqlRs.Columns.Length} column(s) but grouped mode needs the {groupBy.Length} "
                        + "grouping key column(s) (same order as groupBy) followed by the aggregate value column.",
                        ReconcileStatus.InputError);

                var daxRows = BuildMemberRows(daxRs, groupBy.Length, groupBy.Length);   // value at index = #keys ("__recon_value")
                var sqlRows = BuildMemberRows(sqlRs, groupBy.Length, groupBy.Length);   // value = the column right after the keys
                result.DaxRowCount = daxRows.Count;
                result.SqlRowCount = sqlRows.Count;
                truncated = daxRs.Truncated || sqlRs.Truncated;

                var join = ReconcileJoiner.FullOuterJoin(daxRows, sqlRows);
                if (join.DuplicateError != null)
                    return ReconcileRunResult.Fail(join.DuplicateError, ReconcileStatus.InputError);
                result.MissingInDax = join.MissingInDax;
                result.MissingInSql = join.MissingInSql;
                cells.AddRange(join.Cells);

                // Grand total is OPTIONAL in grouped mode, and queried INDEPENDENTLY (contract c) — only when the
                // caller supplies its own total SQL. We never reconstruct it from the member rows.
                if (!string.IsNullOrWhiteSpace(req.SqlGrandTotal))
                {
                    var gt = await BuildGrandTotalCellAsync(live, probe.MeasureName, server, database, token, req.SqlGrandTotal);
                    if (gt.Error != null)
                        return gt.IsShape ? ReconcileRunResult.Fail(gt.Error, ReconcileStatus.InputError) : QueryFail(gt.Stage, gt.Error, result.DaxQuery);
                    cells.Add(gt.Cell);
                    result.DaxElapsedMs += gt.DaxMs; result.SqlElapsedMs += gt.SqlMs;
                }
            }
            else
            {
                // ---- grand-total-only mode: req.Sql IS the total SQL ----
                result.DaxQuery = BuildDaxGrandTotalQuery(probe.MeasureName);
                var gt = await BuildGrandTotalCellAsync(live, probe.MeasureName, server, database, token, req.Sql);
                if (gt.Error != null)
                    return gt.IsShape ? ReconcileRunResult.Fail(gt.Error, ReconcileStatus.InputError) : QueryFail(gt.Stage, gt.Error, result.DaxQuery);
                cells.Add(gt.Cell);
                result.DaxElapsedMs = gt.DaxMs; result.SqlElapsedMs = gt.SqlMs;
                result.DaxExecutedAtUtc = gt.DaxAt; result.SqlExecutedAtUtc = gt.SqlAt;
            }

            // ---- the pure judgement ----
            var judged = MeasureReconciler.Reconcile(cells, policy);
            result.Matches = judged.Matches;
            result.Mismatches = judged.Mismatches;
            result.Unverifiable = judged.Unverifiable;
            result.InvalidInputs = judged.InvalidInputs;
            result.AnyMismatch = judged.AnyMismatch;
            result.HasGrandTotal = judged.HasGrandTotal;
            result.MemberCellsChecked = judged.MemberCellsChecked;
            result.SuspiciouslyLoose = judged.SuspiciouslyLoose;
            result.Cells = judged.Cells;
            result.Truncated = truncated;
            result.Complete = !truncated;
            if (judged.WorstOffender != null)
            {
                var wc = judged.Cells.FirstOrDefault(c => ReferenceEquals(c.Cell, judged.WorstOffender));
                result.WorstKey = judged.WorstOffender.GroupingKey == null || judged.WorstOffender.GroupingKey.Length == 0
                    ? "(grand total)" : string.Join(" | ", judged.WorstOffender.GroupingKey);
                result.WorstExplanation = wc?.Explanation;
            }

            // (contract f/h) A truncated run is never a passing run even if every returned cell matched — downgrade a
            // green verdict to InsufficientCoverage and say why. A real mismatch / input error stands regardless.
            if (truncated && judged.Status == ReconcileStatus.Reconciled)
            {
                result.Status = ReconcileStatus.InsufficientCoverage;
                result.Summary = "Insufficient coverage: the row cap was hit, so not every member was checked: a truncated run "
                    + "cannot certify the measure. Raise maxRows or narrow the grouping, then re-run. "
                    + $"(Of what was checked: {judged.Summary})";
            }
            else
            {
                result.Status = judged.Status;
                result.Summary = truncated ? judged.Summary + " [note: results were truncated at the row cap; coverage is incomplete]" : judged.Summary;
                // (P2-6/contract f) Even a clean Reconciled verified only the members the two queries RETURNED — we do
                // not know the universe of expected members, so say so rather than let it read as total coverage.
                if (result.Status == ReconcileStatus.Reconciled)
                    result.Summary += " Expected-member coverage is UNKNOWN: this reconciles the members observed, not that no member is missing.";
            }

            result.SuggestedNextAction = SuggestNext(result, grouped);
            return result;
        }

        // ---- query builders --------------------------------------------------------------------------

        // The DAX side: SUMMARIZECOLUMNS over the grouping columns + the measure, with a constant "__recon_present"
        // sentinel so a row whose measure is BLANK is RETAINED (SUMMARIZECOLUMNS otherwise drops all-blank rows) — that
        // keeps a present-BLANK member distinguishable from a MISSING one (contract e), which the join relies on.
        private static string BuildDaxMemberQuery(string measureName, string[] groupBy)
        {
            var sb = new StringBuilder();
            sb.Append("EVALUATE\nSUMMARIZECOLUMNS(\n");
            var args = new List<string>();
            foreach (var g in groupBy) args.Add("    " + g);
            args.Add("    \"" + ReconValueCol + "\", " + MeasureBracket(measureName));
            args.Add("    \"" + ReconPresentCol + "\", 1");
            sb.Append(string.Join(",\n", args));
            sb.Append("\n)");
            return sb.ToString();
        }

        // The DAX grand total: the measure evaluated at total filter context, queried INDEPENDENTLY (never rebuilt
        // from the members). ROW normally returns a single row (a BLANK measure => a present-empty value) — we VERIFY
        // the shape below and fail on anything else rather than assume it.
        private static string BuildDaxGrandTotalQuery(string measureName)
            => "EVALUATE\nROW(\"" + ReconValueCol + "\", " + MeasureBracket(measureName) + ")";

        private async Task<GrandTotalOutcome> BuildGrandTotalCellAsync(
            LiveConnection live, string measureName, string server, string database, string token, string sql)
        {
            var daxAt = DateTime.UtcNow;
            var daxRs = await live.ExecuteAsync(BuildDaxGrandTotalQuery(measureName), 10, 120).ConfigureAwait(false);
            if (daxRs.PolicyRefused) return GrandTotalOutcome.Failed("DAX grand-total query", daxRs.Error);
            if (!string.IsNullOrEmpty(daxRs.Error)) return GrandTotalOutcome.Failed("DAX grand-total query", daxRs.Error);
            // ROW(...) always returns exactly one row with one column; anything else is an engine anomaly we fail on
            // rather than fabricate a total from.
            if (daxRs.Rows.Length != 1 || daxRs.Columns.Length == 0)
                return GrandTotalOutcome.Failed("DAX grand-total query", $"expected exactly one total row/value but got {daxRs.Rows.Length} row(s), {daxRs.Columns.Length} column(s).");

            var sqlAt = DateTime.UtcNow;
            var sqlRs = await FabricSqlQuery.ExecuteAsync(server, database, token, sql, 10, 120, CancellationToken.None).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(sqlRs.Error)) return GrandTotalOutcome.Failed("SQL grand-total query", sqlRs.Error);
            // (P1-5) A grand total must be exactly ONE row with a value column. ZERO rows is NOT a present NULL — that
            // would fabricate a judgeable total and could green a run under BlankIsNull. It is a malformed total query
            // (a SUM over an empty set still returns one NULL row; zero rows means it was not an aggregate) — fail
            // closed as an InputError, never coerce it to a present-empty.
            if (sqlRs.Rows.Length != 1)
                return GrandTotalOutcome.Shape("The grand-total SQL returned " + sqlRs.Rows.Length + " row(s): it must aggregate to EXACTLY one row/value at total context (no GROUP BY). Zero rows is not a NULL total.");
            if (sqlRs.Columns.Length == 0)
                return GrandTotalOutcome.Shape("The grand-total SQL returned no value column: it must return the single aggregate value.");

            var daxVal = ExtractOnlyRowLastColumn(daxRs);
            var sqlVal = ExtractOnlyRowLastColumn(sqlRs);
            var cell = new ReconcileCell { GroupingKey = Array.Empty<string>() };   // empty key = grand total
            ApplyCoerced(daxVal, v => cell.Dax = v, () => cell.DaxBlank = true, () => cell.DaxUnsupported = true);
            ApplyCoerced(sqlVal, v => cell.Sql = v, () => cell.SqlNull = true, () => cell.SqlUnsupported = true);
            return new GrandTotalOutcome { Cell = cell, DaxMs = daxRs.ElapsedMs, SqlMs = sqlRs.ElapsedMs, DaxAt = daxAt, SqlAt = sqlAt };
        }

        // The grand-total value is the LAST column of the (guaranteed) single row. Callers verify exactly one row and
        // >= 1 column BEFORE calling this, so a null cell here is a genuine present-empty (NULL/BLANK), never "no row".
        private static CoercedValue ExtractOnlyRowLastColumn(ResultSet rs)
        {
            var row = rs.Rows[0];
            return ReconcileCoercion.Coerce(row[row.Length - 1]);
        }

        private static List<ReconcileSourceRow> BuildMemberRows(ResultSet rs, int keyCount, int valueIndex)
        {
            var rows = new List<ReconcileSourceRow>(rs.Rows.Length);
            foreach (var r in rs.Rows)
            {
                var keys = new object[keyCount];
                var display = new string[keyCount];
                for (var i = 0; i < keyCount; i++)
                {
                    keys[i] = i < r.Length ? r[i] : null;
                    display[i] = ReconcileKeyEncoder.Display(keys[i]);
                }
                var val = ReconcileCoercion.Coerce(valueIndex < r.Length ? r[valueIndex] : null);
                rows.Add(new ReconcileSourceRow { DisplayKey = display, MatchKey = ReconcileKeyEncoder.ComposeKey(keys), Value = val });
            }
            return rows;
        }

        private static void ApplyCoerced(CoercedValue v, Action<decimal> setValue, Action setEmpty, Action setUnsupported)
        {
            if (v.Unsupported) setUnsupported();
            else if (v.Empty) setEmpty();
            else if (v.Value.HasValue) setValue(v.Value.Value);
            else setEmpty();
        }

        // ---- model read: resolve the measure + the grouping columns + the Fabric SQL coordinates ------
        private static MeasureSourceProbe ResolveMeasureAndSource(Model m, string measureRef, string[] groupBy)
        {
            if (string.IsNullOrWhiteSpace(measureRef))
                return new MeasureSourceProbe { Error = "A measure ref is required (a name or 'measure:Name'); run list_measures to see them." };
            if (!(ObjectRefs.Resolve(m, measureRef) is Measure meas))
                return new MeasureSourceProbe { Error = $"{measureRef} is not a measure: pass a measure ref (a name or 'measure:Name'); run list_measures to see them." };

            // (P1-3) Resolve every grouping entry to a REAL model column and emit an engine-constructed DAX ref. Raw
            // caller text is never spliced into the DAX query — that is how one array element could smuggle extra
            // SUMMARIZECOLUMNS extension columns and make the runner read the wrong value column. An entry that does not
            // resolve to exactly one column is refused, naming it.
            var refs = new string[groupBy.Length];
            for (var i = 0; i < groupBy.Length; i++)
            {
                if (!TryResolveGroupColumn(m, groupBy[i], out var daxRef, out var why))
                    return new MeasureSourceProbe { Error = $"groupBy entry '{groupBy[i]}' {why}. Pass each grouping column as a 'column:Table/Column' ref or a plain 'Table'[Column]; run list_columns to see them." };
                refs[i] = daxRef;
            }

            var probe = new MeasureSourceProbe { MeasureName = meas.Name, GroupByRefs = refs };
            var mapping = BuildReconcileMappingReview(m, ObjectRefs.For(meas));
            probe.Server = mapping.DetectedServer;
            probe.Database = mapping.DetectedDatabase;
            return probe;
        }

        private static ReconcileMappingReview BuildReconcileMappingReview(Model m, string measureRef)
        {
            if (string.IsNullOrWhiteSpace(measureRef))
                return new ReconcileMappingReview { Error = "A measure ref is required; run list_measures to see them." };
            if (!(ObjectRefs.Resolve(m, measureRef) is Measure measure))
                return new ReconcileMappingReview { MeasureRef = measureRef, Error = $"{measureRef} is not a measure; run list_measures to see them." };

            var related = new HashSet<Table>();
            var seen = new HashSet<Measure>();
            var queue = new Queue<Measure>();
            queue.Enqueue(measure); seen.Add(measure);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                IEnumerable<object> deps;
                try { deps = current.DependsOn.Keys.Cast<object>().ToArray(); }
                catch { continue; }   // a stale/unparsed dependency tree removes confidence; model-wide candidates still surface
                foreach (var dep in deps)
                {
                    if (dep is Column col && col.Table != null) related.Add(col.Table);
                    else if (dep is Table table) related.Add(table);
                    else if (dep is Measure next && seen.Add(next)) queue.Enqueue(next);
                }
            }

            var candidates = SqlSourceDiscovery.Find(m).Select(c => new ReconcileSourceCandidate
            {
                ModelTable = c.ModelTable, Server = c.Server, Database = c.Database, Schema = c.Schema,
                Entity = c.Entity, Relevant = related.Contains(m.Tables[c.ModelTable]),
            }).ToList();
            candidates = candidates
                .GroupBy(c => string.Join("\u001f", c.ModelTable, c.Server, c.Database, c.Schema, c.Entity), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(c => c.Relevant)
                .ThenBy(c => c.ModelTable, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var complete = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Server) && !string.IsNullOrWhiteSpace(c.Database)).ToList();
            var preferred = complete.Any(c => c.Relevant) ? complete.Where(c => c.Relevant).ToList() : complete;
            var coordinates = preferred
                .GroupBy(c => (Server: c.Server.Trim(), Database: c.Database.Trim()), new CoordinateComparer())
                .Select(g => g.Key)
                .ToList();
            var ambiguous = coordinates.Count > 1;
            var detected = coordinates.Count == 1 ? coordinates[0] : default;
            var note = ambiguous
                ? "The measure reaches more than one SQL source, so no endpoint was guessed. Choose the endpoint and database explicitly."
                : coordinates.Count == 1
                    ? (preferred.Any(c => c.Relevant) ? "Detected from the measure's dependency tables." : "Detected from the model's SQL-backed tables; no dependency-specific source was available.")
                    : "No SQL endpoint and database could be derived from this model. Enter them explicitly.";
            return new ReconcileMappingReview
            {
                MeasureRef = ObjectRefs.For(measure),
                MeasureName = measure.Name,
                DetectedServer = detected.Server,
                DetectedDatabase = detected.Database,
                Ambiguous = ambiguous,
                Sources = candidates.ToArray(),
                Note = note,
                SuggestedNextAction = coordinates.Count == 1 ? null : "supply server + database",
            };
        }

        private sealed class CoordinateComparer : IEqualityComparer<(string Server, string Database)>
        {
            public bool Equals((string Server, string Database) x, (string Server, string Database) y) =>
                string.Equals(x.Server, y.Server, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Database, y.Database, StringComparison.OrdinalIgnoreCase);
            public int GetHashCode((string Server, string Database) obj) =>
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Server ?? "") * 397
                ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Database ?? "");
        }

        // Resolve one grouping entry to a model column and return an engine-constructed, escaped DAX ref ('Table'[Col]).
        // Accepts either a canonical 'column:Table/Column' ref or a DAX-style 'Table'[Column] / Table[Column]. Anything
        // that does not resolve to exactly one real column fails (with a reason) — no raw text ever reaches the query.
        private static bool TryResolveGroupColumn(Model m, string entry, out string daxRef, out string why)
        {
            daxRef = null; why = null;
            var e = (entry ?? "").Trim();
            if (e.Length == 0) { why = "is empty"; return false; }

            Column col = null;
            if (e.StartsWith("column:", StringComparison.OrdinalIgnoreCase))
            {
                col = ObjectRefs.Resolve(m, e) as Column;
                if (col == null) { why = "did not resolve to a model column"; return false; }
            }
            else if (TryParseDaxColumn(e, out var tableName, out var columnName))
            {
                if (!m.Tables.Contains(tableName)) { why = $"names table '{tableName}', which is not in the model"; return false; }
                var table = m.Tables[tableName];
                if (!table.Columns.Contains(columnName)) { why = $"names column '{columnName}' on '{tableName}', which does not exist"; return false; }
                col = table.Columns[columnName];
            }
            else
            {
                why = "is not a recognizable column reference ('column:Table/Column' or 'Table'[Column])";
                return false;
            }

            daxRef = ColumnDaxRef(col);
            return true;
        }

        // Parse a DAX-style column reference: 'Quoted Table'[Column] or Table[Column]. Deliberately strict — a bare
        // base-column reference only, no extra tokens — so nothing beyond one table+column can be smuggled through.
        private static bool TryParseDaxColumn(string s, out string table, out string column)
        {
            table = column = null;
            var open = s.IndexOf('[');
            if (open <= 0 || !s.EndsWith("]", StringComparison.Ordinal)) return false;
            var tablePart = s.Substring(0, open).Trim();
            column = s.Substring(open + 1, s.Length - open - 2);
            if (column.IndexOf('[') >= 0 || column.IndexOf(']') >= 0) return false;   // one bracketed segment only
            if (tablePart.Length >= 2 && tablePart[0] == '\'' && tablePart[tablePart.Length - 1] == '\'')
                tablePart = tablePart.Substring(1, tablePart.Length - 2).Replace("''", "'");
            table = tablePart;
            return table.Length > 0 && column.Length > 0;
        }

        // Build an injection-proof DAX column reference from a RESOLVED model column: the table name is single-quoted
        // (own quotes doubled) and the column name's ']' is doubled — canonical DAX escaping, from the model, never
        // from caller text.
        private static string ColumnDaxRef(Column col)
        {
            var table = (col.Table?.Name ?? "").Replace("'", "''");
            var name = (col.Name ?? "").Replace("]", "]]");
            return "'" + table + "'[" + name + "]";
        }

        // ---- small helpers ----------------------------------------------------------------------------
        // The attempted DAX rides on the failure too (sol finding): the query that FAILED is exactly the
        // evidence the drill-down needs; discarding it left failed outcomes with nothing to show.
        private static ReconcileRunResult QueryFail(string stage, string error, string daxQuery = null)
        {
            var f = ReconcileRunResult.Fail($"{stage} failed: {error}. A failed query is never a passing run; fix it and re-run.",
                ReconcileStatus.InsufficientCoverage);
            f.DaxQuery = daxQuery;
            return f;
        }

        private static string SuggestNext(ReconcileRunResult r, bool grouped)
        {
            switch (r.Status)
            {
                case ReconcileStatus.Mismatch:
                    return $"inspect the worst offender ({r.WorstKey}); SQL is the ground truth, so check the measure's DAX or the relationship feeding it";
                case ReconcileStatus.InsufficientCoverage:
                    return grouped ? "raise maxRows or narrow the grouping so every member is checked"
                                   : "supply groupBy + a member SQL to verify per-member; a matching total can hide the blank-row trap";
                case ReconcileStatus.InputError:
                    return "fix the request per the message, then re-run";
                case ReconcileStatus.NothingVerifiable:
                    return "supply comparable cells (a value on each side) so at least one member can be judged";
                default:
                    return null;   // Reconciled — terminal
            }
        }

        private static string MeasureBracket(string name) => "[" + (name ?? "").Replace("]", "]]") + "]";
        private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        private static string Inv(double x) => x.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static bool TryParseBlankPolicy(string s, out BlankPolicy policy)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "zero": case "blankiszero": policy = BlankPolicy.BlankIsZero; return true;
                case "null": case "blankisnull": policy = BlankPolicy.BlankIsNull; return true;
                case "distinct": case "blankisdistinct": policy = BlankPolicy.BlankIsDistinct; return true;
                default: policy = BlankPolicy.BlankIsZero; return false;
            }
        }

        private sealed class MeasureSourceProbe
        {
            public string MeasureName;
            public string[] GroupByRefs = Array.Empty<string>();   // engine-constructed, escaped DAX column refs
            public string Server;
            public string Database;
            public string Error;
        }

        private sealed class GrandTotalOutcome
        {
            public ReconcileCell Cell;
            public long DaxMs;
            public long SqlMs;
            public DateTime DaxAt;
            public DateTime SqlAt;
            public string Error;
            public string Stage;
            public bool IsShape;   // a malformed grand-total SQL (wrong row/column shape) is an InputError, not an execution failure

            public static GrandTotalOutcome Failed(string stage, string error) => new GrandTotalOutcome { Stage = stage, Error = error };
            public static GrandTotalOutcome Shape(string error) => new GrandTotalOutcome { Stage = "SQL grand-total query", Error = error, IsShape = true };
        }
    }
}
