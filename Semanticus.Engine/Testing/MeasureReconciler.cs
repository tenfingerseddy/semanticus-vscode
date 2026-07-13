using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Semanticus.Engine
{
    // ============================================================================================
    //  MeasureReconciler — the correctness spine of the Tests tab.
    //
    //  A measure test proves a DAX measure against an INDEPENDENT ground truth computed by SQL on the
    //  source (a Fabric SQL endpoint). The two numbers arrive over SEPARATE connections — DAX over
    //  XMLA, SQL over TDS — already joined by the caller into ReconcileCells, one per filter context
    //  (grand total, per-dimension-member, sliced, empty member, time-intelligence variant). This
    //  component owns only the JUDGEMENT: it opens no connection, runs no query, has no I/O. Pure in,
    //  verdicts out — so every subtle rule below is unit-testable OFFLINE, which is the whole point.
    //
    //  Why this is not a one-liner: a naive `dax == sql` destroys trust on day one. Three things break
    //  it — (1) float noise (SUM vs SUMX, XMLA vs TDS coercion perturb the last bits), (2) BLANK/NULL/0
    //  are three different things and which of them are "equal" depends on the measure, and (3) the
    //  VertiPaq blank-row trap, where an invalid relationship makes the GRAND TOTAL tie while per-member
    //  breakdowns are wrong — so a reconciler that only checks the total LIES. Each is handled below.
    //
    //  A fourth thing broke it in review: per-cell verdicts are NOT a run verdict. !AnyMismatch is not a
    //  pass — a total-only run, an all-unverifiable run, or a run fed malformed input all have zero
    //  mismatches yet none of them earned "All clear". So the run now carries a closed-set Status plus
    //  COVERAGE facts (HasGrandTotal, MemberCellsChecked, invalid-input count), and green (Reconciled) is
    //  a positive claim — at least one verifiable member match — not merely the absence of failures.
    //
    //  An InputError run still reports honest per-cell tallies: every classifiable cell is classified and
    //  counted before the status fails closed, so a duplicate grand total can never HIDE a member
    //  mismatch behind the input complaint.
    //
    //  Numeric-equality lineage: this echoes DaxBench.ValuesEqual (rel 1e-7 tolerates float summation
    //  reordering, 1e-9 absolute floor covers near-zero) — see TolerancePolicy.Default. It DIFFERS in
    //  one deliberate way: DaxBench compares two DAX rewrites where neither is "truth", so it uses a
    //  symmetric max(|a|,|b|) denominator; here SQL IS the ground truth, so the relative term is scaled
    //  by abs(sql) per the task's rule. The BLANK/NULL policy is entirely new.
    // ============================================================================================

    /// <summary>How a DAX BLANK() and a SQL NULL relate to each other and to a numeric 0. This is the
    /// subtlest knob in the reconciler: a SUM over no rows is BLANK in DAX but NULL in SQL, and whether
    /// that should equal 0 depends ENTIRELY on the measure's intent — so the caller must choose; we
    /// never silently guess per cell.</summary>
    public enum BlankPolicy
    {
        /// <summary>BLANK and NULL both read as an exact 0. Right for additive measures (Total Sales
        /// over an empty slice is legitimately 0). BLANK-vs-0, NULL-vs-0 and BLANK-vs-NULL all MATCH;
        /// BLANK-vs-500 FAILS with the honest magnitude in the delta.</summary>
        BlankIsZero,

        /// <summary>BLANK is approximately NULL ("no value"), and BOTH differ from 0. Right for measures
        /// where "no data" is meaningfully distinct from zero (averages, ratios, presence flags). A value
        /// facing an empty is a MISMATCH; empty-facing-empty is a MATCH.</summary>
        BlankIsNull,

        /// <summary>BLANK, NULL and 0 are all mutually distinct. The strictest reading: we refuse to
        /// equate a DAX BLANK with a SQL NULL. Empty-vs-value is a MISMATCH; BLANK-vs-NULL is
        /// UNVERIFIABLE — not wrong, but not provably equal, so a human decides.</summary>
        BlankIsDistinct,
    }

    /// <summary>The match threshold. A pair matches iff
    /// <c>abs(dax - sql) &lt;= max(Absolute, Relative * abs(sql))</c> — NEVER <c>==</c>. SQL is the
    /// ground truth, so it is the relative denominator; when <c>sql == 0</c> the relative term vanishes
    /// and the Absolute floor decides (this is the divide-by-zero AND the near-zero-noise guard).</summary>
    public sealed class TolerancePolicy
    {
        /// <summary>Relative tolerance, as a fraction of abs(sql) (e.g. 1e-7 = 0.00001%; 1.0 = 100%).
        /// Must be finite and &gt;= 0 — the reconciler rejects the run otherwise (an infinite window
        /// greens everything).</summary>
        public double Relative { get; set; }

        /// <summary>Absolute floor — the tolerance when sql is 0 or tiny. Must be finite and &gt;= 0.</summary>
        public double Absolute { get; set; }

        /// <summary>How BLANK/NULL/0 compare. Has no sane default across measure kinds, so the caller
        /// owns it (see <see cref="BlankPolicy"/>).</summary>
        public BlankPolicy Blank { get; set; }

        /// <summary>The codebase's suggested starting point: echoes <see cref="DaxBench.ValuesEqual"/>
        /// (rel 1e-7 tolerates float-reorder noise; 1e-9 absolute floor) with
        /// <see cref="BlankPolicy.BlankIsZero"/> — the additive-measure assumption most first tests want.
        /// This is NOT applied implicitly: <see cref="MeasureReconciler.Reconcile"/> rejects a null policy
        /// as an input error, so a caller that wants these defaults must pass this value EXPLICITLY. That
        /// keeps the BLANK/NULL/0 reading a deliberate choice rather than a silent guess. Non-additive
        /// measures should pick a different <see cref="BlankPolicy"/>.</summary>
        public static TolerancePolicy Default =>
            new TolerancePolicy { Relative = 1e-7, Absolute = 1e-9, Blank = BlankPolicy.BlankIsZero };
    }

    /// <summary>One already-joined comparison: a single filter context where the DAX measure and the SQL
    /// ground truth are lined up. The caller does a FULL OUTER JOIN of the two result sets by
    /// <see cref="GroupingKey"/> and emits one cell per key (a grand total has an EMPTY GroupingKey).
    ///
    /// Each side carries FOUR distinguishable states, because "no value" is not one thing:
    ///   * a NUMBER          -> Dax has a value and DaxBlank == false.  (0 is a number.)
    ///   * present but EMPTY -> DaxBlank == true. DAX returned BLANK(); Dax is ignored.
    ///   * ABSENT (no row)   -> Dax == null AND DaxBlank == false. The join found nothing on this side.
    ///   * UNSUPPORTED       -> DaxUnsupported == true (Dax null, DaxBlank false). The runner met a value
    ///     it could not represent as a decimal (NaN, +/-Infinity, out-of-range, conversion error) and
    ///     hands the FACT over instead of clamping or dropping — judged UnsupportedNumeric, never coerced.
    /// The SQL side mirrors this with Sql / SqlNull / SqlUnsupported. The blank/null FLAG is exactly what
    /// separates a genuine present-empty from a missing row — without it, semantic #4 (missing-cell) is
    /// unencodable. A missing row is otherwise treated as an empty (a dropped DAX row == BLANK for a
    /// measure), except a missing row facing a real value is the loud disagreement the explanation calls
    /// out by name. The four states are MUTUALLY EXCLUSIVE per side: an unsupported flag alongside a
    /// value or a blank flag is contradictory provenance and rejects the run as an input error.</summary>
    public sealed class ReconcileCell
    {
        public string[] GroupingKey { get; set; } = Array.Empty<string>();
        public decimal? Dax { get; set; }
        public decimal? Sql { get; set; }
        public bool DaxBlank { get; set; }
        public bool SqlNull { get; set; }

        /// <summary>The runner could not represent the DAX result as a decimal (NaN, +/-Infinity,
        /// out-of-range, conversion failure). Set INSTEAD of a value: Dax must be null and DaxBlank false,
        /// or the cell is contradictory input. Maps straight to
        /// <see cref="ReconcileVerdict.UnsupportedNumeric"/> with the side named in the explanation.</summary>
        public bool DaxUnsupported { get; set; }

        /// <summary>The SQL-side mirror of <see cref="DaxUnsupported"/> (e.g. a SQL decimal(38,...)
        /// exceeding .NET decimal — the problem is not DAX-only).</summary>
        public bool SqlUnsupported { get; set; }
    }

    /// <summary>The four ways a cell can land. Only <see cref="Mismatch"/> counts as a failure;
    /// <see cref="UnverifiableBlank"/> ("we honestly can't judge this BLANK vs NULL") and
    /// <see cref="UnsupportedNumeric"/> ("a side or the difference isn't representable, so we won't fake
    /// a number") are both surfaced, never hidden, but neither fails the run on its own — they count as
    /// unverifiable.</summary>
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public enum ReconcileVerdict { Match, Mismatch, UnverifiableBlank, UnsupportedNumeric }

    /// <summary>A closed set of RUN-level outcomes — the single label the UI shows next to Coverage
    /// (design invariant I2: grade and coverage never appear apart). It exists because a per-cell tally
    /// is not a run verdict: the absence of a mismatch is NOT a pass. Deriving green from
    /// <c>!AnyMismatch</c> is exactly how a total-only, all-unverifiable, or malformed run earns a false
    /// clean bill — the whole class of bug this component exists to prevent.</summary>
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public enum ReconcileStatus
    {
        /// <summary>Green. At least one VERIFIABLE member-level match, zero mismatches, zero invalid
        /// inputs. The only status the UI may render as a pass — and only an UNCAVEATED pass when
        /// <see cref="ReconcileResult.Unverifiable"/> is also zero (the summary carries the caveat).</summary>
        Reconciled,

        /// <summary>At least one real cell mismatched (mirrors <see cref="ReconcileResult.AnyMismatch"/>).
        /// The failure state.</summary>
        Mismatch,

        /// <summary>Nothing mismatched, but too little was verified to certify. The canonical case is a
        /// matching grand total with no member cells: the total alone cannot rule out the blank-row
        /// trap, so a matching total is never green on its own.</summary>
        InsufficientCoverage,

        /// <summary>No cell could be judged pass or fail at all (every cell unverifiable, or nothing to
        /// reconcile). Never green — "we couldn't check anything" is not "everything is fine".</summary>
        NothingVerifiable,

        /// <summary>The input itself was malformed — a null policy, a non-finite/negative tolerance, null
        /// or contradictory cells, or duplicate grand totals — so the run cannot be trusted until it is
        /// fixed. The per-cell tallies are still reported honestly (a member mismatch is never hidden
        /// behind the input complaint), but the status fails closed.</summary>
        InputError,
    }

    /// <summary>The verdict for one cell, with a human <see cref="Explanation"/> and the numbers behind
    /// it. <see cref="Delta"/> = DAX minus SQL (sign = the model's bias vs ground truth); it is null when
    /// there is no numeric comparison to make (a structural mismatch, an unverifiable blank, or a
    /// difference that overflows decimal). <see cref="RelDelta"/> = abs(Delta)/abs(sql) — the ranking
    /// metric for <see cref="ReconcileResult.WorstOffender"/>.</summary>
    public sealed class CellVerdict
    {
        public ReconcileCell Cell { get; set; }
        public ReconcileVerdict Verdict { get; set; }
        public string Explanation { get; set; }
        public decimal? Delta { get; set; }
        public double? RelDelta { get; set; }
    }

    /// <summary>The whole reconciliation. Read <see cref="Status"/> for the run verdict — NOT
    /// <see cref="AnyMismatch"/>, which is only "did anything fail" and cannot distinguish a genuine pass
    /// from a run that verified nothing. Green is <see cref="ReconcileStatus.Reconciled"/> and nothing
    /// else — and an UNCAVEATED green additionally requires <see cref="Unverifiable"/> == 0. The coverage
    /// facts (<see cref="HasGrandTotal"/>, <see cref="MemberCellsChecked"/>, <see cref="InvalidInputs"/>)
    /// say how much of what the caller asked for was actually checked.</summary>
    public sealed class ReconcileResult
    {
        public CellVerdict[] Cells { get; set; } = Array.Empty<CellVerdict>();
        public int Matches { get; set; }
        public int Mismatches { get; set; }

        /// <summary>Cells we could neither pass nor fail: BLANK-vs-NULL under BlankIsDistinct
        /// (<see cref="ReconcileVerdict.UnverifiableBlank"/>) and values or differences too large to
        /// represent as a decimal (<see cref="ReconcileVerdict.UnsupportedNumeric"/>). Surfaced so the UI
        /// can flag "needs a human" honestly; never counted as a pass.</summary>
        public int Unverifiable { get; set; }

        /// <summary>Input entries that produced no verdict at all — null cells, cells with contradictory
        /// provenance (an unsupported flag alongside a value/blank), or every cell when the run was
        /// rejected before classification (bad tolerance, null policy). Counted, never silently dropped,
        /// so the conservation identity holds against the INPUT cardinality:
        /// <c>Matches + Mismatches + Unverifiable + InvalidInputs == (# input cells)</c>.</summary>
        public int InvalidInputs { get; set; }

        /// <summary>True iff at least one cell mismatched. Kept for callers that only need the failure
        /// bit, but it is NOT the pass gate — a false AnyMismatch does not imply a pass. Use
        /// <see cref="Status"/>.</summary>
        public bool AnyMismatch { get; set; }

        /// <summary>The run verdict. The one field the UI grades on.</summary>
        public ReconcileStatus Status { get; set; }

        /// <summary>A grand-total cell (empty grouping key) was SUPPLIED in the input. Provenance, not
        /// coverage: the total may still have been unjudgeable (a null or contradictory cell) — in which
        /// case the run is already <see cref="ReconcileStatus.InputError"/> — so never read this as "the
        /// total was verified"; coverage is <see cref="MemberCellsChecked"/> and the per-cell verdicts.</summary>
        public bool HasGrandTotal { get; set; }

        /// <summary>How many per-member (non-grand-total) cells were actually VERIFIED — matched or
        /// mismatched, i.e. neither unverifiable nor invalid. This is the coverage denominator: a
        /// total-only run has zero here, which is precisely why a matching total alone is
        /// <see cref="ReconcileStatus.InsufficientCoverage"/> rather than green.</summary>
        public int MemberCellsChecked { get; set; }

        /// <summary>The tolerance window is suspiciously permissive: the relative tolerance exceeds the
        /// 1% ceiling, OR the absolute tolerance alone admits &gt;1% error on the largest value observed
        /// in this run's verifiable cells. The run still executed — the caller may mean it — but a
        /// "match" at this width can hide a large real error, so the UI must warn.</summary>
        public bool SuspiciouslyLoose { get; set; }

        public string Summary { get; set; }

        /// <summary>The mismatching cell with the largest relative delta — the one the UI jumps to.
        /// Null when nothing mismatched.</summary>
        public ReconcileCell WorstOffender { get; set; }
    }

    /// <summary>Pure, offline reconciler: compare a DAX result and a SQL ground truth, cell by cell,
    /// under an explicit tolerance + blank policy, and roll the cells up into a run-level
    /// <see cref="ReconcileStatus"/>. No connections, no query building — see the header.</summary>
    public static class MeasureReconciler
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        // Above this relative tolerance a "match" is suspiciously permissive (1% of ground truth). We do
        // not block — the caller may mean it — but we flag it so the UI never presents a loose green as tight.
        private const double LooseRelativeCeiling = 0.01;

        public static ReconcileResult Reconcile(IEnumerable<ReconcileCell> cells, TolerancePolicy policy)
        {
            // Materialize once. We need the INPUT cardinality (not the survivor count) so coverage and the
            // conservation identity are honest about what the caller actually asked us to verify (P1.2).
            var input = (cells ?? Enumerable.Empty<ReconcileCell>()).ToList();

            // (P2c) An explicit policy is mandatory. Silently defaulting could equate BLANK/NULL/0 without
            // the caller ever choosing that reading — the exact silent guess this component exists to
            // prevent. TolerancePolicy.Default stays available; the caller must pass it deliberately.
            if (policy == null)
                return WholeRunInputError(input,
                    "No tolerance policy supplied. Pass a TolerancePolicy explicitly (TolerancePolicy.Default "
                    + "is the additive-measure starting point) so the BLANK/NULL/0 reading is a choice, not a guess.");

            // (P3) An undefined BlankPolicy (a bad cast or a corrupt deserialize) has no defensible
            // semantics — validated UP FRONT, before any short-circuit, so an empty or all-null input can't
            // slip an undefined policy through unnoticed. Fail loud: this is a caller/programming bug, not data.
            if (policy.Blank != BlankPolicy.BlankIsZero && policy.Blank != BlankPolicy.BlankIsNull
                && policy.Blank != BlankPolicy.BlankIsDistinct)
                throw new ArgumentOutOfRangeException(nameof(policy), policy.Blank, "Undefined BlankPolicy value.");

            // (P2a) Tolerances must be finite and non-negative. NaN, +/-Infinity, or a negative window can
            // never define a sane threshold — an infinite threshold greens every numeric cell — so we
            // refuse the run with a teaching summary rather than clamp silently (the old behaviour) or
            // throw mid-loop.
            if (!IsFiniteNonNegative(policy.Absolute) || !IsFiniteNonNegative(policy.Relative))
                return WholeRunInputError(input,
                    $"Invalid tolerance (Absolute={Num(policy.Absolute)}, Relative={Num(policy.Relative)}). "
                    + "Both must be finite and >= 0; relative is a fraction of abs(sql), so 1.0 = 100%, not 1%.");

            // (contract c) The grand total must be exactly one cell: multiple empty-key cells make the
            // blank-row-trap narrative order-dependent (which "total" wins the FirstOrDefault?). We do NOT
            // short-circuit, though — every classifiable cell below is still classified and tallied, so the
            // input complaint can never HIDE a member mismatch. Only the run status fails closed.
            var grandTotals = input.Count(c => c != null && IsGrandTotal(c));
            var duplicateTotals = grandTotals > 1;

            // Classify every classifiable cell; count the rest. A null entry is malformed input, and so is a
            // cell whose provenance contradicts itself (an unsupported flag alongside a value/blank — the
            // runner's coercion is lying about what it saw). Both are COUNTED via InvalidInputs (P1.2),
            // never silently dropped: the old `if (cell != null)` skip understated coverage.
            var verdicts = new List<CellVerdict>(input.Count);
            int nullCells = 0, contradictoryCells = 0;
            foreach (var cell in input)
            {
                if (cell == null) { nullCells++; continue; }
                if (IsContradictory(cell)) { contradictoryCells++; continue; }
                verdicts.Add(Classify(cell, policy.Blank, policy.Absolute, policy.Relative));
            }
            var invalidInputs = nullCells + contradictoryCells;

            int matches = 0, mismatches = 0, unverifiable = 0;
            foreach (var v in verdicts)
            {
                if (v.Verdict == ReconcileVerdict.Match) matches++;
                else if (v.Verdict == ReconcileVerdict.Mismatch) mismatches++;
                else unverifiable++;   // UnverifiableBlank + UnsupportedNumeric: judged neither pass nor fail
            }

            // From the INPUT, not the judged verdicts: this field answers "was a total supplied?", and the
            // two readings diverge when the only total is invalid (null/contradictory) — a post-filter read
            // would claim "no total" while the fault accounting above saw one. Provenance stays input-true;
            // there is no fail-open risk because such a run is already InputError, and coverage is
            // MemberCellsChecked, never this flag.
            var hasGrandTotal = grandTotals > 0;
            // Coverage counts VERIFIABLE members only (I1: an Unknown is not coverage). A total-only run,
            // or one whose members were all unverifiable, has zero here — the InsufficientCoverage signal.
            var memberCellsChecked = verdicts.Count(v => !IsGrandTotal(v.Cell) && IsVerifiable(v.Verdict));
            var verifiable = matches + mismatches;

            // (P2a) A very loose tolerance still runs — the caller may mean it — but the UI must be able to
            // warn. Relative has a universal ceiling (1% of ground truth). Absolute does NOT — 0.5 is loose
            // for unit prices and tight for revenue — so we judge it against THIS run's data: if the
            // absolute window alone admits >1% error on the largest magnitude observed among the verifiable
            // cells, a "match" can hide an error of exactly the size the relative ceiling exists to catch.
            // We take the max over BOTH sides, not just sql: with sql=0 and dax=100 the ground truth alone
            // offers no scale while the window silently greens a 100-unit error. An all-zero run (max
            // magnitude 0) genuinely has no scale to be loose against, so it never flags on this rule.
            double maxObserved = 0.0;
            foreach (var v in verdicts)
            {
                if (!IsVerifiable(v.Verdict)) continue;
                if (v.Cell.Dax.HasValue && !v.Cell.DaxBlank) maxObserved = Math.Max(maxObserved, Math.Abs((double)v.Cell.Dax.Value));
                if (v.Cell.Sql.HasValue && !v.Cell.SqlNull) maxObserved = Math.Max(maxObserved, Math.Abs((double)v.Cell.Sql.Value));
            }
            var suspiciouslyLoose = policy.Relative > LooseRelativeCeiling
                                 || (maxObserved > 0.0 && policy.Absolute > LooseRelativeCeiling * maxObserved);

            var worst = SelectWorst(verdicts);
            var status = RunStatus(mismatches, invalidInputs, duplicateTotals, verifiable, memberCellsChecked);
            return new ReconcileResult
            {
                Cells = verdicts.ToArray(),
                Matches = matches,
                Mismatches = mismatches,
                Unverifiable = unverifiable,
                InvalidInputs = invalidInputs,
                AnyMismatch = mismatches > 0,      // (3) unverifiable/invalid does NOT flip this — only real mismatches
                Status = status,
                HasGrandTotal = hasGrandTotal,
                MemberCellsChecked = memberCellsChecked,
                SuspiciouslyLoose = suspiciouslyLoose,
                WorstOffender = worst?.Cell,
                Summary = BuildSummary(verdicts, matches, mismatches, unverifiable, status, worst, suspiciouslyLoose,
                                       nullCells, contradictoryCells, grandTotals),
            };
        }

        // Green requires a positive claim, not merely the absence of failure. Precedence, most severe first:
        //   InputError  — the input was malformed (invalid cells or an ambiguous grand total); we refuse to
        //                 certify anything derived from it. The tallies still surface every count.
        //   Mismatch    — at least one real disagreement.
        //   Nothing/InsufficientCoverage — no failure, but not enough was verified to say "pass".
        //   Reconciled  — >= 1 verifiable MEMBER match, zero mismatches, zero invalid inputs.
        private static ReconcileStatus RunStatus(int mismatches, int invalidInputs, bool duplicateTotals, int verifiable, int memberChecked)
        {
            if (invalidInputs > 0 || duplicateTotals) return ReconcileStatus.InputError;
            if (mismatches > 0)      return ReconcileStatus.Mismatch;
            if (verifiable == 0)     return ReconcileStatus.NothingVerifiable;    // all unverifiable, or empty
            if (memberChecked == 0)  return ReconcileStatus.InsufficientCoverage; // total-only: the trap can hide here
            return ReconcileStatus.Reconciled;
        }

        private enum Side { Value, Blank, Missing }   // Blank = present BLANK/NULL; Missing = no row this side

        private static CellVerdict Classify(ReconcileCell c, BlankPolicy blank, double absTol, double relTol)
        {
            // (contract b) A side the runner preclassified as unsupported (NaN/Infinity/out-of-range) is a
            // FACT about the data, not a policy question: no BlankPolicy can turn "we couldn't represent it"
            // into a number, so it maps straight to UnsupportedNumeric with the side named. Contradictory
            // flags were already rejected upstream, so here the flag is the whole story for that side.
            if (c.DaxUnsupported || c.SqlUnsupported)
            {
                var side = c.DaxUnsupported && c.SqlUnsupported ? "both sides are"
                         : c.DaxUnsupported ? "the DAX side is" : "the SQL side is";
                return V(c, ReconcileVerdict.UnsupportedNumeric, null, null,
                    $"{DaxText(c)} vs {SqlText(c)} - {side} not representable as a decimal; not verifiable.");
            }

            var ds = c.DaxBlank ? Side.Blank : (c.Dax.HasValue ? Side.Value : Side.Missing);
            var ss = c.SqlNull ? Side.Blank : (c.Sql.HasValue ? Side.Value : Side.Missing);

            // A missing measure row is indistinguishable from BLANK/NULL (DAX drops all-blank rows), so we
            // collapse Missing into "empty" and let BlankPolicy judge it — EXCEPT we keep the missing-ness to
            // WORD the explanation ("absent from the source/model", semantic #4). This is deliberate: under
            // BlankIsZero a missing side reads as 0, so missing-vs-tiny can legitimately MATCH; under the
            // non-coercing policies empty-vs-value fails regardless. A missing row facing a real VALUE is the
            // loudest disagreement (the two systems disagree a slice exists at all) and always surfaces.
            bool daxEmpty = ds != Side.Value;
            bool sqlEmpty = ss != Side.Value;
            bool daxMissing = ds == Side.Missing;
            bool sqlMissing = ss == Side.Missing;

            switch (blank)
            {
                case BlankPolicy.BlankIsZero:
                {
                    // Additive-measure reading: BLANK/NULL/absent all == exact 0.
                    var dax = daxEmpty ? 0m : c.Dax.Value;
                    var sql = sqlEmpty ? 0m : c.Sql.Value;
                    var (verdict, delta, rel) = Compare(dax, sql, absTol, relTol);
                    if (verdict == ReconcileVerdict.UnsupportedNumeric)
                        return V(c, ReconcileVerdict.UnsupportedNumeric, null, null, OverflowText(c));
                    var absence = AbsencePhrase(daxMissing, sqlMissing, daxEmpty, sqlEmpty);
                    var reason = verdict == ReconcileVerdict.Match ? "within tolerance" : "exceeds tolerance";
                    var expl = $"{DaxText(c)} vs {SqlText(c)}, delta={Dec(delta.Value)}{RelText(rel)} - {reason}"
                             + (daxEmpty || sqlEmpty ? " (empty read as 0)" : "")
                             + (verdict == ReconcileVerdict.Mismatch && absence != null ? $"; {absence}" : "");
                    return V(c, verdict, delta, rel, expl);
                }

                case BlankPolicy.BlankIsNull:
                {
                    if (daxEmpty && sqlEmpty)
                        return V(c, ReconcileVerdict.Match, null, null,
                            $"{DaxText(c)} vs {SqlText(c)} - both empty (no value); equal under BlankIsNull.");
                    if (daxEmpty || sqlEmpty)
                    {
                        var absence = AbsencePhrase(daxMissing, sqlMissing, daxEmpty, sqlEmpty);
                        var why = absence ?? "a value on one side only (empty is not 0 under BlankIsNull)";
                        return V(c, ReconcileVerdict.Mismatch, null, null, $"{DaxText(c)} vs {SqlText(c)} - {why}.");
                    }
                    return Numeric(c, absTol, relTol);
                }

                case BlankPolicy.BlankIsDistinct:
                {
                    if (daxEmpty && sqlEmpty)
                        // Neither wrong nor provably equal: we will not equate a DAX BLANK with a SQL NULL (or
                        // two absent rows). This is the one honest "can't verify this cell" outcome — a human call.
                        return V(c, ReconcileVerdict.UnverifiableBlank, null, null,
                            $"{DaxText(c)} vs {SqlText(c)} - distinct/absent on both sides; not equatable under BlankIsDistinct.");
                    if (daxEmpty || sqlEmpty)
                    {
                        var absence = AbsencePhrase(daxMissing, sqlMissing, daxEmpty, sqlEmpty);
                        var why = absence ?? "a value on one side only (blank, null and 0 are all distinct)";
                        return V(c, ReconcileVerdict.Mismatch, null, null, $"{DaxText(c)} vs {SqlText(c)} - {why}.");
                    }
                    return Numeric(c, absTol, relTol);
                }

                default:
                    // Unreachable: Reconcile validates the enum up front (P3). Kept as a safety net so a new
                    // BlankPolicy member cannot be silently mis-judged if this switch is not extended with it.
                    throw new ArgumentOutOfRangeException(nameof(blank), blank, "Undefined BlankPolicy value.");
            }
        }

        // Value-vs-Value numeric compare wrapped as a CellVerdict (both sides guaranteed to hold a number).
        private static CellVerdict Numeric(ReconcileCell c, double absTol, double relTol)
        {
            var (verdict, delta, rel) = Compare(c.Dax.Value, c.Sql.Value, absTol, relTol);
            if (verdict == ReconcileVerdict.UnsupportedNumeric)
                return V(c, ReconcileVerdict.UnsupportedNumeric, null, null, OverflowText(c));
            var reason = verdict == ReconcileVerdict.Match ? "within tolerance" : "exceeds tolerance";
            return V(c, verdict, delta, rel, $"{DaxText(c)} vs {SqlText(c)}, delta={Dec(delta.Value)}{RelText(rel)} - {reason}.");
        }

        // (1) The float-tolerance heart. NEVER '=='. Matches iff |dax-sql| <= max(abs, rel*|sql|). We
        // subtract in DECIMAL (exact — the fetched values are decimal, so this avoids float cancellation
        // manufacturing a spurious delta) and only cross to DOUBLE for the fuzzy magnitude/threshold, which
        // is inherently approximate and is where the double-typed tolerances naturally live. abs(sql) is the
        // relative denominator because SQL is the ground truth; sql==0 => the relative term is 0 => the
        // absolute floor alone decides (the sql==0 guard).
        //
        // (P2b) The exact subtraction can OVERFLOW: two representable decimals of opposite sign near
        // +/-7.9e28 sum past decimal's range. We refuse to clamp, round, drop, or crash — any of which would
        // fabricate a verdict — and return UnsupportedNumeric (delta/rel null) so the run counts it as
        // unverifiable. (double casts never throw: decimal always fits in double, if lossily.)
        private static (ReconcileVerdict verdict, decimal? delta, double? rel) Compare(decimal dax, decimal sql, double absTol, double relTol)
        {
            decimal delta;
            try { delta = dax - sql; }
            catch (OverflowException) { return (ReconcileVerdict.UnsupportedNumeric, null, null); }

            var mag = Math.Abs((double)delta);
            var thr = Math.Max(absTol, relTol * Math.Abs((double)sql));
            var match = mag <= thr;
            // Reported relative delta: against |sql| (matching the match rule). When sql==0 fall back to
            // |dax| so a 0-vs-nonzero cell ranks as maximally divergent (rel = 1) instead of undefined;
            // both-zero => rel 0.
            var denom = sql != 0m ? Math.Abs((double)sql) : Math.Abs((double)dax);
            double? rel = denom != 0.0 ? mag / denom : 0.0;
            return (match ? ReconcileVerdict.Match : ReconcileVerdict.Mismatch, delta, rel);
        }

        // (4) The canonical missing-cell wording, when exactly one side is a MISSING row and the other has
        // data. Null when absence is not the story (both empty, or a present BLANK rather than a missing row).
        private static string AbsencePhrase(bool daxMissing, bool sqlMissing, bool daxEmpty, bool sqlEmpty)
        {
            if (daxMissing && !sqlEmpty) return "present in the source, absent from the model";
            if (sqlMissing && !daxEmpty) return "present in the model, absent from the source";
            return null;
        }

        private static CellVerdict SelectWorst(List<CellVerdict> cells)
        {
            CellVerdict worst = null;
            foreach (var c in cells)
            {
                if (c.Verdict != ReconcileVerdict.Mismatch) continue;   // only mismatches are "offenders"
                if (worst == null || MoreSevere(c, worst)) worst = c;
            }
            return worst;
        }

        // (5) Rank by relative delta (largest first) — where the UI jumps. A structural mismatch with no
        // computable relDelta (a missing row, or empty-vs-value under a non-coercing policy) is treated as
        // +infinity so it always wins, tie-broken by |delta|; that surfaces "a whole slice is wrong" ahead
        // of a merely large percentage.
        private static bool MoreSevere(CellVerdict a, CellVerdict b)
        {
            double ar = a.RelDelta ?? double.PositiveInfinity, br = b.RelDelta ?? double.PositiveInfinity;
            if (ar > br) return true;
            if (ar < br) return false;
            double ad = a.Delta.HasValue ? Math.Abs((double)a.Delta.Value) : double.PositiveInfinity;
            double bd = b.Delta.HasValue ? Math.Abs((double)b.Delta.Value) : double.PositiveInfinity;
            return ad > bd;
        }

        private static string BuildSummary(List<CellVerdict> cells, int matches, int mismatches, int unverifiable,
                                           ReconcileStatus status, CellVerdict worst, bool loose,
                                           int nullCells, int contradictoryCells, int grandTotals)
        {
            var invalidInputs = nullCells + contradictoryCells;
            var total = cells.Count + invalidInputs;   // == the input cell count
            var looseNote = loose
                ? " [warning: the tolerance window is very loose - a 'match' can hide a large real error]"
                : "";

            switch (status)
            {
                case ReconcileStatus.InputError:
                {
                    // Name EVERY input fault AND the full honest tallies: the classifiable cells were still
                    // classified, so a member mismatch is reported right alongside the complaint — an input
                    // error must fail the run closed without hiding what the data itself said.
                    var faults = new List<string>();
                    if (grandTotals > 1) faults.Add($"{grandTotals} grand-total cells supplied (exactly one expected)");
                    if (nullCells > 0) faults.Add($"{nullCells} null cell{(nullCells == 1 ? "" : "s")}");
                    if (contradictoryCells > 0) faults.Add($"{contradictoryCells} contradictory cell{(contradictoryCells == 1 ? "" : "s")} (an unsupported flag alongside a value or a blank/null flag)");
                    return $"Input error: {string.Join("; ", faults)}. Among the cells that could be checked: "
                         + $"{matches} matched, {mismatches} mismatched, {unverifiable} unverifiable. "
                         + "Fix the input before trusting this run.";
                }

                case ReconcileStatus.NothingVerifiable:
                    if (cells.Count == 0) return "No cells to reconcile.";
                    return $"Nothing verifiable: none of {total} cells could be judged pass or fail "
                         + $"({unverifiable} unverifiable, e.g. BLANK vs NULL under BlankIsDistinct). No pass or fail can be claimed.";

                case ReconcileStatus.InsufficientCoverage:
                    // (3) The whole reason this component exists: a matching total is NOT a pass. Name the trap.
                    return "Insufficient coverage: the grand total reconciles but no member-level cell was verified. "
                         + "A matching total alone cannot rule out the blank-row trap - an invalid relationship can "
                         + "reparent rows onto the blank member while the total still ties. Add per-member cells to verify."
                         + (unverifiable > 0 ? $" ({unverifiable} unverifiable.)" : "");

                case ReconcileStatus.Reconciled:
                    if (unverifiable > 0)
                        // Real member matches with zero mismatches, but some cells couldn't be checked — honest
                        // green with a caveat. Deliberately NOT "All clear": that phrase implies nothing was left unchecked.
                        return $"Reconciled: {matches} verifiable cells match within tolerance; {unverifiable} could not be checked (needs review).{looseNote}";
                    return $"All clear: {matches}/{total} cells reconcile within tolerance.{looseNote}";

                case ReconcileStatus.Mismatch:
                default:
                {
                    var unv = unverifiable > 0 ? $" ({unverifiable} unverifiable)" : "";
                    // (3) The VertiPaq blank-row trap: an invalid relationship reparents orphan rows onto the blank
                    // row, so the GRAND TOTAL still ties while per-member breakdowns are wrong. A total-only check
                    // would report a false "match"; we NAME the pattern so the matching total is never trusted alone.
                    var grand = cells.FirstOrDefault(v => IsGrandTotal(v.Cell));
                    var members = cells.Count(v => !IsGrandTotal(v.Cell));
                    var memberMismatches = cells.Count(v => v.Verdict == ReconcileVerdict.Mismatch && !IsGrandTotal(v.Cell));
                    if (grand != null && grand.Verdict == ReconcileVerdict.Match && memberMismatches > 0)
                        return $"Grand total matches but {memberMismatches} of {members} members disagree - a matching "
                             + $"total can hide per-member errors (e.g. an invalid relationship reparenting rows onto the "
                             + $"blank row). Worst: {WorstText(worst)}.{unv}{looseNote}";

                    return $"{mismatches} of {total} cells mismatch. Worst: {WorstText(worst)}.{unv}{looseNote}";
                }
            }
        }

        // A whole-run rejection BEFORE classification (null policy / invalid tolerance): the policy is what
        // gives cells meaning, so without a valid one no cell can earn a verdict — every input is an invalid
        // input and the conservation identity still holds (0 + 0 + 0 + input.Count == input.Count). This is
        // deliberately different from the per-cell faults, where classification still proceeds.
        private static ReconcileResult WholeRunInputError(List<ReconcileCell> input, string summary) =>
            new ReconcileResult
            {
                Cells = Array.Empty<CellVerdict>(),
                Matches = 0,
                Mismatches = 0,
                Unverifiable = 0,
                InvalidInputs = input.Count,
                AnyMismatch = false,
                Status = ReconcileStatus.InputError,
                HasGrandTotal = input.Any(c => c != null && IsGrandTotal(c)),
                MemberCellsChecked = 0,
                SuspiciouslyLoose = false,
                WorstOffender = null,
                Summary = summary,
            };

        // (contract e) Provenance must be mutually exclusive per side: an unsupported flag alongside a value
        // or a blank/null flag means the runner's coercion contradicts itself — we refuse to guess which
        // signal to believe (silently letting one win is how a fabricated number gets certified).
        private static bool IsContradictory(ReconcileCell c) =>
            (c.DaxUnsupported && (c.Dax.HasValue || c.DaxBlank)) ||
            (c.SqlUnsupported && (c.Sql.HasValue || c.SqlNull));

        private static bool IsVerifiable(ReconcileVerdict v) => v == ReconcileVerdict.Match || v == ReconcileVerdict.Mismatch;
        private static bool IsFiniteNonNegative(double x) => double.IsFinite(x) && x >= 0.0;
        private static bool IsGrandTotal(ReconcileCell c) => c.GroupingKey == null || c.GroupingKey.Length == 0;

        private static string WorstText(CellVerdict w)
        {
            if (w == null) return "(none)";
            var d = w.Delta.HasValue ? $", delta={Dec(w.Delta.Value)}{RelText(w.RelDelta)}" : "";
            return $"{KeyText(w.Cell)} [{DaxText(w.Cell)} vs {SqlText(w.Cell)}{d}]";
        }

        private static CellVerdict V(ReconcileCell c, ReconcileVerdict v, decimal? delta, double? rel, string expl) =>
            new CellVerdict { Cell = c, Verdict = v, Delta = delta, RelDelta = rel, Explanation = expl };

        private static string OverflowText(ReconcileCell c) =>
            $"{DaxText(c)} vs {SqlText(c)} - difference is not representable as a decimal; not verifiable.";

        private static string Dec(decimal v) => v.ToString(Ci);
        private static string Num(double v) => v.ToString(Ci);
        private static string RelText(double? rel) => rel.HasValue ? $" (rel={(rel.Value * 100).ToString("0.###", Ci)}%)" : "";
        private static string DaxText(ReconcileCell c) =>
            c.DaxUnsupported ? "DAX=(unsupported)" : c.DaxBlank ? "DAX=BLANK" : c.Dax.HasValue ? "DAX=" + Dec(c.Dax.Value) : "DAX=(absent)";
        private static string SqlText(ReconcileCell c) =>
            c.SqlUnsupported ? "SQL=(unsupported)" : c.SqlNull ? "SQL=NULL" : c.Sql.HasValue ? "SQL=" + Dec(c.Sql.Value) : "SQL=(absent)";
        private static string KeyText(ReconcileCell c) => IsGrandTotal(c) ? "(grand total)" : string.Join(" | ", c.GroupingKey);
    }
}
