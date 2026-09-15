using System;

namespace Semanticus.Engine
{
    /// <summary>The request for one measure reconciliation run (the impure runner's input). The grouping SQL and the
    /// optional independent grand-total SQL are the caller's INDEPENDENTLY-authored ground truth — the engine never
    /// writes them (golden rule 1). The blank policy is REQUIRED (no silent default — the BLANK/NULL/0 reading must be
    /// a deliberate choice, contract d/e), as are finite, non-negative tolerances.</summary>
    /// <summary>One input of a "Compare with source" check, as a drawer needs to present it.</summary>
    public sealed class ReconcileField
    {
        public string Field { get; set; }   // the paramsJson property name, camelCase
        public string Label { get; set; }   // what the person is being asked for, in their words
        public string Help { get; set; }    // one plain sentence
    }

    /// <summary>
    /// What a "Compare with source" drawer must keep VISIBLE and what it may hide behind Advanced. The split is not a
    /// UI opinion: <see cref="Required"/> is exactly what the engine refuses to run without, and
    /// <see cref="RequiredForAVerdict"/> is the one input the engine happily runs without while never being able to
    /// certify the result.
    ///
    /// Kept here, beside the request it describes, so the two cannot drift; ReconcileContractTests pins every list
    /// and proves each refusal is real.
    /// </summary>
    public static class ReconcileContract
    {
        /// <summary>The engine returns an InputError without these. They are the drawer's visible fields.</summary>
        public static readonly ReconcileField[] Required =
        {
            new ReconcileField { Field = "measureRef", Label = "Which calculation",
                Help = "The measure whose number you are checking." },
            new ReconcileField { Field = "sql", Label = "The SQL you trust",
                Help = "Your own query that works out the same number from the source. Semanticus never writes it for you." },
            new ReconcileField { Field = "blankPolicy", Label = "What a blank means",
                Help = "Choose one: blank counts as zero, blank is its own thing like NULL, or blank, NULL and zero are all different. There is no default, because the choice changes the answer." },
        };

        /// <summary>The engine RUNS without this, but the result can never be better than "couldn't check": a matching
        /// grand total cannot rule out rows hiding on the blank row. A drawer that files this under Advanced without
        /// saying so is offering a check that can never pass.</summary>
        public static readonly ReconcileField[] RequiredForAVerdict =
        {
            new ReconcileField { Field = "groupBy", Label = "Groups to compare",
                Help = "The columns to break the number down by, such as year and category. Without them only the grand total is compared, and a matching total cannot prove the detail matches." },
        };

        /// <summary>Tuning. Safe to keep behind Advanced: every one of these has a sound behaviour when left alone.</summary>
        public static readonly ReconcileField[] Optional =
        {
            new ReconcileField { Field = "sqlSourceId", Label = "SQL source",
                Help = "The saved source to read from, picked by name. Without one the check uses the endpoint saved on the check itself." },
            new ReconcileField { Field = "sqlGrandTotal", Label = "Separate total query",
                Help = "A second query for the overall total, so the total is asked for on its own rather than added up from the rows." },
            new ReconcileField { Field = "filterDax", Label = "Only where",
                Help = "Narrow the model side the same way the report was narrowed. Narrow your SQL to match, because Semanticus never edits your query." },
            new ReconcileField { Field = "toleranceAbsolute", Label = "Allowed difference",
                Help = "How far apart two numbers may be and still count as the same. Zero means they must match exactly." },
            new ReconcileField { Field = "toleranceRelative", Label = "Allowed difference, as a share",
                Help = "The same idea as a fraction of the source number. 0.01 means one percent." },
            new ReconcileField { Field = "maxRows", Label = "How many rows to compare",
                Help = "A cap, so a huge breakdown does not run away. A capped run is never reported as a complete one." },
        };
    }

    public sealed class ReconcileRequest
    {
        /// <summary>The measure to test (a name or a 'measure:Name' ref).</summary>
        public string MeasureRef { get; set; }

        /// <summary>The DAX grouping columns, e.g. ["'Date'[Year]", "'Product'[Category]"]. Empty/null =
        /// grand-total-only (which is InsufficientCoverage by design — a matching total cannot rule out the
        /// blank-row trap).</summary>
        public string[] GroupBy { get; set; } = Array.Empty<string>();

        /// <summary>The ground-truth SQL. In grouped mode it must return the grouping key columns (same order as
        /// <see cref="GroupBy"/>) followed by the aggregate value column. In grand-total-only mode it aggregates to a
        /// single row / single value.</summary>
        public string Sql { get; set; }

        /// <summary>Optional independent grand-total SQL for grouped mode — a SEPARATE query at total filter context
        /// (a single row / value). Supplied so the total is queried independently, not reconstructed from the member
        /// rows (contract c). Ignored in grand-total-only mode.</summary>
        public string SqlGrandTotal { get; set; }

        /// <summary>Optional DAX filter predicates (comma-separated, the CALCULATETABLE argument shape, e.g.
        /// <c>'Date'[Year] = 2026, 'Product'[Category] = "Bikes"</c>) applied to the DAX side of the comparison.
        /// A check saved from a FILTERED visual must narrow the same way the visual did, or a calculation filtered
        /// to one year silently becomes a check over the whole model. The SQL side is the caller's own text and is
        /// NEVER rewritten: the author applies the same narrowing there (the drawer says so).</summary>
        public string FilterDax { get; set; }

        /// <summary>Absolute tolerance floor. Must be finite and &gt;= 0.</summary>
        public double ToleranceAbsolute { get; set; }

        /// <summary>Relative tolerance, a FRACTION of abs(sql) (1.0 = 100%, not 1%). Must be finite and &gt;= 0.</summary>
        public double ToleranceRelative { get; set; }

        /// <summary>REQUIRED: "zero" | "null" | "distinct" (BlankIsZero / BlankIsNull / BlankIsDistinct). No default.</summary>
        public string BlankPolicy { get; set; }

        /// <summary>Member-row cap for the DAX + SQL grouped queries (0 = a sane default). A truncated run is never a
        /// passing run — it is marked incomplete.</summary>
        public int MaxRows { get; set; }

        /// <summary>The NAMED SQL source this check uses (SqlSourceRecord.Id), saved once in Connections and picked
        /// by name. When set it WINS over <see cref="Server"/>, <see cref="Database"/>, <see cref="AuthMode"/> and
        /// <see cref="TenantId"/> below; when absent those inline values are used, which is how every definition
        /// saved before named sources existed keeps working. A named source that has been removed is reported, never
        /// quietly replaced by the inline values.</summary>
        public string SqlSourceId { get; set; }

        /// <summary>Optional Fabric SQL endpoint override; when omitted the runner derives it from the model's Fabric
        /// SQL source. A named <see cref="SqlSourceId"/> supersedes this.</summary>
        public string Server { get; set; }
        public string Database { get; set; }

        /// <summary>Entra auth mode for the SQL token (default azcli), and an optional tenant id — mirrors the schema
        /// introspection path.</summary>
        public string AuthMode { get; set; }
        public string TenantId { get; set; }
    }

    /// <summary>The request for one expected-total test (kind measureValue). The number is the caller's trusted
    /// answer. Optional filter narrows the measure; optional provenance is stored with the test and shown in evidence.</summary>
    public sealed class MeasureValueRequest
    {
        public string MeasureRef { get; set; }
        public string ExpectedValue { get; set; }
        public string FilterColumn { get; set; }
        public string FilterValue { get; set; }
        public string FilterDax { get; set; }

        /// <summary>Optional DAX the test evaluates INSTEAD of the bound measure's own formula — the DAX Lab
        /// "Test this expression" path, so an edited formula can be checked before it is applied to the model.
        /// Either a scalar expression (wrapped in ROW, and narrowed by <see cref="FilterDax"/>) or a complete
        /// EVALUATE query, which is run verbatim and read from its first cell (a complete query already carries
        /// its own filter context, so FilterDax is not applied to it). The measure binding still travels, so the
        /// test stays attached to the object it is about.</summary>
        public string ExpressionDax { get; set; }

        public string Provenance { get; set; }
        public double? ToleranceAbsolute { get; set; }
        public double? ToleranceRelative { get; set; }
    }

    /// <summary>Read-only source-mapping review for a saved reconciliation. Detection comes from the measure's
    /// dependency tables and their partitions; overrides are echoed separately so the UI never confuses a human
    /// choice with model-derived metadata. TestConnection performs only a connectivity probe, never the accepted SQL.</summary>
    public sealed class ReconcileMappingRequest
    {
        public string MeasureRef { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public string AuthMode { get; set; }
        public string TenantId { get; set; }
        public bool TestConnection { get; set; }
    }

    public sealed class ReconcileSourceCandidate
    {
        public string ModelTable { get; set; }
        public string Server { get; set; }
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Entity { get; set; }
        public bool Relevant { get; set; }
    }

    public sealed class ReconcileMappingReview
    {
        public string MeasureRef { get; set; }
        public string MeasureName { get; set; }
        public string DetectedServer { get; set; }
        public string DetectedDatabase { get; set; }
        public string EffectiveServer { get; set; }
        public string EffectiveDatabase { get; set; }
        public bool Ambiguous { get; set; }
        public ReconcileSourceCandidate[] Sources { get; set; } = Array.Empty<ReconcileSourceCandidate>();
        public bool Tested { get; set; }
        public bool Connected { get; set; }
        public long ElapsedMs { get; set; }
        public string TestError { get; set; }
        public string ApprovalId { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
        public string SuggestedNextAction { get; set; }
    }

    /// <summary>The reconciliation run result: the reconciler's run-level verdict and coverage facts, plus the runner's
    /// own completeness facts (row counts, missing-on-each-side, truncation) and the effective tolerances/policy so a
    /// green result is auditable. <see cref="Status"/> is the field to grade on (never <see cref="AnyMismatch"/> alone);
    /// an UNCAVEATED green is <see cref="ReconcileStatus.Reconciled"/> AND <see cref="Unverifiable"/> == 0 AND
    /// <see cref="Complete"/>.</summary>
    public sealed class ReconcileRunResult
    {
        public ReconcileStatus Status { get; set; }
        public string Summary { get; set; }

        public int Matches { get; set; }
        public int Mismatches { get; set; }
        public int Unverifiable { get; set; }
        public int InvalidInputs { get; set; }
        public bool AnyMismatch { get; set; }

        public bool HasGrandTotal { get; set; }
        public int MemberCellsChecked { get; set; }
        public bool SuspiciouslyLoose { get; set; }

        // Effective knobs — persisted so the run is auditable after the fact (contract h).
        public double ToleranceAbsolute { get; set; }
        public double ToleranceRelative { get; set; }
        public string BlankPolicy { get; set; }

        // Completeness facts the reconciler cannot see (contract f).
        public int DaxRowCount { get; set; }
        public int SqlRowCount { get; set; }
        public int MissingInDax { get; set; }
        public int MissingInSql { get; set; }
        public bool Truncated { get; set; }
        /// <summary>Every query returned WITHOUT hitting the client row cap. This is NOT "all expected members were
        /// checked" — see <see cref="CoverageKnown"/>. It is only the truncation/error precondition for a green.</summary>
        public bool Complete { get; set; }

        /// <summary>Whether the COMPLETE expected membership is known (contract f: label unknown rather than complete).
        /// The runner cannot enumerate the universe of members a-priori — it verifies the members the two queries
        /// returned, not that no member is missing — so this is <c>false</c> here. A green run is therefore always
        /// "reconciled over the members observed", with expected-member coverage UNKNOWN, never a claim of totality.</summary>
        public bool CoverageKnown { get; set; }

        public long DaxElapsedMs { get; set; }
        public long SqlElapsedMs { get; set; }

        /// <summary>The exact DAX the runner executed (member query, plus the independent grand-total query when
        /// one ran) — evidence for the UI's side-by-side view. The SQL side is the caller's own text (the request);
        /// only the DAX is engine-authored, so only the DAX needs surfacing back.</summary>
        public string DaxQuery { get; set; }

        // (contract g) The two sides run sequentially over INDEPENDENT connections, so they may observe different source
        // snapshots. We cannot guarantee isolation; we record when each side executed so drift is at least visible and
        // auditable. SnapshotNote states the caveat plainly.
        public DateTime? DaxExecutedAtUtc { get; set; }
        public DateTime? SqlExecutedAtUtc { get; set; }
        public string SnapshotNote { get; set; }

        public string WorstKey { get; set; }
        public string WorstExplanation { get; set; }

        /// <summary>The full per-cell verdicts (for the UI / drill-down). The MCP tool summarises these; it does not
        /// dump them by default.</summary>
        public CellVerdict[] Cells { get; set; } = Array.Empty<CellVerdict>();

        /// <summary>Set on a hard failure or a refusal (not connected, no SQL coordinates, a query error, a bad
        /// request). Never set alongside a green <see cref="Status"/>.</summary>
        public string Error { get; set; }

        /// <summary>True when <see cref="Error"/> is an agent-policy refusal (the run never executed), so a wrapper can
        /// route "get approval" vs "fix the request".</summary>
        public bool PolicyRefused { get; set; }

        /// <summary>The exact pending approval created by an Ask refusal, so the human UI can route to it.</summary>
        public string ApprovalId { get; set; }

        /// <summary>Harness result-contract navigation: the exact next call/action, or null when terminal.</summary>
        public string SuggestedNextAction { get; set; }

        internal static ReconcileRunResult Fail(string error, ReconcileStatus status, string next = null, bool refused = false, string approvalId = null) =>
            new ReconcileRunResult { Error = error, Status = status, Summary = error, SuggestedNextAction = next, PolicyRefused = refused, ApprovalId = approvalId, Complete = false };
    }
}
