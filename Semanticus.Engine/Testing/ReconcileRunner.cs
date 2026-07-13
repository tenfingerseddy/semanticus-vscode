using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Semanticus.Engine
{
    // ============================================================================================
    //  ReconcileRunner — the IMPURE half's PURE core.
    //
    //  MeasureReconciler is the judge; it can only be as honest as the cells it is handed
    //  (docs/tests-tab-runner-contract.md). This file owns the three pieces the runner must get
    //  right BEFORE the judge ever sees a cell — and factors them out of the live query paths so
    //  every one is unit-testable OFFLINE (the whole point, mirroring the judge):
    //    * ReconcileCoercion — a raw provider cell -> decimal | present-empty | not-representable
    //      (contract b + e), never clamped, dropped, or thrown.
    //    * ReconcileKeyEncoder — a grouping cell -> a culture-invariant, type-tagged, delimiter-safe
    //      composite key that distinguishes null / blank / empty / numeric-vs-text lookalikes /
    //      timezone variants (contract c) so the join lines the RIGHT rows up.
    //    * ReconcileJoiner — a FULL OUTER JOIN by that key (contract a), one cell per union key
    //      including one-sided rows, with duplicate detection that refuses a Cartesian.
    //  The live query execution + grand-total wiring lives in LocalEngine.Testing.cs; it feeds these.
    // ============================================================================================

    /// <summary>A provider cell coerced to the reconciler's per-side vocabulary: exactly one of a
    /// finite decimal <see cref="Value"/>, a present <see cref="Empty"/> (DAX BLANK / SQL NULL), or
    /// <see cref="Unsupported"/> (NaN / +/-Infinity / out-of-range / conversion failure — a FACT the
    /// runner hands over instead of faking a number). Mutually exclusive by construction.</summary>
    public readonly struct CoercedValue
    {
        public decimal? Value { get; }
        public bool Empty { get; }
        public bool Unsupported { get; }
        private CoercedValue(decimal? value, bool empty, bool unsupported) { Value = value; Empty = empty; Unsupported = unsupported; }

        public static CoercedValue OfValue(decimal d) => new CoercedValue(d, false, false);
        public static readonly CoercedValue PresentEmpty = new CoercedValue(null, true, false);
        public static readonly CoercedValue NotRepresentable = new CoercedValue(null, false, true);
    }

    /// <summary>Turns a raw boxed provider value (ADOMD / SqlClient) into a <see cref="CoercedValue"/>.
    /// The value KIND is preserved before conversion (contract b): a null provider cell is a present
    /// BLANK/NULL (a MISSING row is a join outcome, not a cell value — the joiner owns it); a numeric
    /// value converts to decimal only when finitely representable; everything else — NaN, Infinity,
    /// a double past decimal's range, a SQL decimal(38,...) the provider could not hand us
    /// (<see cref="ReconcileValues.UnsupportedCell"/>), or a non-numeric type — becomes
    /// <see cref="CoercedValue.NotRepresentable"/>, never clamped or thrown.</summary>
    public static class ReconcileCoercion
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static CoercedValue Coerce(object raw)
        {
            if (raw == null || raw is DBNull) return CoercedValue.PresentEmpty;
            // The runner marks a provider cell it could not even READ (a SQL decimal(38,...) overflowing
            // .NET decimal on GetValue) with this sentinel rather than letting the whole query throw.
            if (ReferenceEquals(raw, ReconcileValues.UnsupportedCell)) return CoercedValue.NotRepresentable;

            switch (raw)
            {
                case decimal d: return CoercedValue.OfValue(d);
                case double db: return FromDouble(db);
                case float f: return FromDouble(f);
                // A measure/ground-truth column that comes back as text, a bool, a date, or a guid is not a
                // NUMBER we can reconcile — say so honestly rather than invent 0/1. (A numeric-looking string
                // is deliberately NOT parsed: the provider typed it as text, so it is not a numeric result.)
                case bool _:
                case string _:
                case char _:
                case DateTime _:
                case DateTimeOffset _:
                case Guid _:
                case byte[] _:
                    return CoercedValue.NotRepresentable;
                case sbyte _:
                case byte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                    return CoercedValue.OfValue(Convert.ToDecimal(raw, Ci));   // every integral type fits decimal
                default:
                    // An unknown numeric-ish type (BigInteger, a provider wrapper): try invariant conversion,
                    // and treat any failure as not-representable rather than propagating an exception.
                    try { return CoercedValue.OfValue(Convert.ToDecimal(raw, Ci)); }
                    catch { return CoercedValue.NotRepresentable; }
            }
        }

        private static CoercedValue FromDouble(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return CoercedValue.NotRepresentable;
            // decimal's range is ~+/-7.9e28; a double past it cannot convert. Guard the bounds AND catch the
            // boundary-rounding overflow — the checked (decimal) cast throws exactly there.
            if (d < (double)decimal.MinValue || d > (double)decimal.MaxValue) return CoercedValue.NotRepresentable;
            try { return CoercedValue.OfValue((decimal)d); }
            catch (OverflowException) { return CoercedValue.NotRepresentable; }
        }
    }

    /// <summary>Encodes a grouping-key cell into a type-tagged, culture-invariant, delimiter-safe token,
    /// and composes the per-dimension tokens into ONE match key. The tags are what keep the join honest
    /// (contract c): a text "2020" (<c>S:2020</c>) never matches a numeric 2020 (<c>#:2020</c>); a null
    /// member (<c>N</c>) never collapses into the empty grand-total key; a DateTimeOffset carries its
    /// offset while a bare DateTime carries its Kind, so timezone variants stay distinct. Integer widths
    /// are normalised (an Int64 2020 from DAX and an int 2020 from SQL both canonicalise to <c>#:2020</c>)
    /// so the SAME logical member lines up across two providers, while collation/case/whitespace are left
    /// ORDINAL on purpose — normalise them deliberately at the query, not via ambient culture here.</summary>
    public static class ReconcileKeyEncoder
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private const char Delim = '\u001F';   // ASCII unit separator — vanishingly unlikely in real data, and escaped anyway

        /// <summary>The type-tagged token for one dimension value. Never empty (a null member is <c>N</c>),
        /// so a real blank member can never masquerade as the empty grand-total key.</summary>
        public static string EncodePart(object v)
        {
            switch (v)
            {
                case null:
                case DBNull _:
                    return "N";
                case string s:
                    return "S:" + s;                                   // empty string -> "S:" (distinct from null "N")
                case bool b:
                    return "B:" + (b ? "1" : "0");
                case DateTimeOffset dto:
                    return "TZ:" + dto.ToString("o", Ci);              // carries the offset -> timezone-distinct
                case DateTime dt:
                    return "T:" + (int)dt.Kind + ":" + dt.ToString("o", Ci);
                case Guid g:
                    return "G:" + g.ToString("N", Ci);
                case byte[] bytes:
                    return "X:" + Convert.ToBase64String(bytes);
                case char c:
                    return "S:" + c;
                default:
                    return Numeric(v);
            }
        }

        // Exact integral values fold to one canonical "#" token so integer width / int-vs-decimal spelling never
        // splits a logical member across the two providers (2020L, 2020, 2020.0m -> "#:2020"). DECIMAL is exact, so it
        // stays on "#". FLOATING keys are the trap: (decimal)double is LOSSY — double.Epsilon narrows to 0 and would
        // collide with a real 0 (finding P1-4). A non-integral float therefore gets its OWN round-trippable token
        // ("#F:" via G17 — the format .NET guarantees parses back to the same double, so two DISTINCT doubles never
        // encode alike). A float that is EXACTLY integral AND within +/-2^53 (where a double represents integers
        // exactly) shares the "#:" integer token so 2020.0d still lines up with a 2020 integer member; ABOVE 2^53 a
        // double cannot represent every integer, so an integral double there stays on "#F:" and will NOT fold onto a
        // bigint's "#:" token — the two are then reported as distinct (fail-loud: a possible-mismatch surfaces, never a
        // false match). This is deliberate and pinned by a test.
        private static string Numeric(object v)
        {
            switch (v)
            {
                case sbyte _: case byte _: case short _: case ushort _:
                case int _: case uint _: case long _: case ulong _:
                case decimal _:
                    return "#:" + Canon(Convert.ToDecimal(v, Ci));
                case double d:
                    return DoubleToken(d);
                case float f:
                    return DoubleToken(f);   // widening float->double is exact, so this is a faithful representation
                default:
                    // An unknown type we did not tag above: keep it distinct and stable, tagged as text so it can
                    // never accidentally equal a real numeric member.
                    return "S:" + Convert.ToString(v, Ci);
            }
        }

        // 2^53 — the largest magnitude at which every integer is EXACTLY representable as a double. Below it an
        // integral double is a real integer we can fold onto the "#:" token; above it (or non-integral / NaN / Inf) we
        // must keep the value distinct via a lossless round-trip ("G17" round-trips every double), never narrow it.
        private const double ExactIntegerCeiling = 9007199254740992d;

        private static string DoubleToken(double d)
        {
            if (!double.IsNaN(d) && !double.IsInfinity(d) && d == Math.Floor(d) && Math.Abs(d) <= ExactIntegerCeiling)
                return "#:" + ((long)d).ToString(Ci);
            return "#F:" + d.ToString("G17", Ci);
        }

        private static string Canon(decimal d)
        {
            var s = d.ToString(Ci);
            if (s.IndexOf('.') >= 0) s = s.TrimEnd('0').TrimEnd('.');
            return s.Length == 0 || s == "-0" ? "0" : s;
        }

        /// <summary>Compose the per-dimension tokens into one delimiter-safe composite key. Each token is
        /// escaped so a value containing the delimiter (or a backslash) can never forge a boundary, and so two
        /// different arities can never encode to the same string.</summary>
        public static string ComposeKey(IEnumerable<object> parts)
            => string.Join(Delim.ToString(), (parts ?? Enumerable.Empty<object>()).Select(p => Escape(EncodePart(p))));

        private static string Escape(string token)
            => token.Replace("\\", "\\\\").Replace(Delim.ToString(), "\\u");

        /// <summary>A human-readable, culture-invariant rendering of one dimension value for the cell's display
        /// key (what the reconciler prints). Never empty for a member — a null shows as "(null)", an empty
        /// string as "(empty)" — so a member row keeps a non-empty GroupingKey and stays distinct from the
        /// grand total.</summary>
        public static string Display(object v)
        {
            switch (v)
            {
                case null:
                case DBNull _: return "(null)";
                case string s: return s.Length == 0 ? "(empty)" : s;
                case DateTime dt: return dt.ToString("o", Ci);
                case DateTimeOffset dto: return dto.ToString("o", Ci);
                case bool b: return b ? "true" : "false";
                case IFormattable f: return f.ToString(null, Ci);
                default: return Convert.ToString(v, Ci) ?? "(null)";
            }
        }
    }

    /// <summary>Shared sentinels for cells the provider could not hand over as a normal value.</summary>
    public static class ReconcileValues
    {
        /// <summary>A boxed marker the SQL runner substitutes for a cell it could not READ (e.g. a
        /// decimal(38,...) that overflows .NET decimal on <c>GetValue</c>) — so ONE unrepresentable cell
        /// becomes a per-side Unsupported verdict instead of aborting the whole query. Reference-compared.</summary>
        public static readonly object UnsupportedCell = new object();
    }

    /// <summary>One already-coerced source row from a single provider (DAX or SQL): its per-dimension display
    /// key, the composite match key, and the coerced measure/ground-truth value. A grand total is handled by
    /// the runner, not here — every row this joiner sees is a member.</summary>
    public sealed class ReconcileSourceRow
    {
        public string[] DisplayKey { get; set; } = Array.Empty<string>();
        public string MatchKey { get; set; } = "";
        public CoercedValue Value { get; set; }
    }

    /// <summary>The result of the full outer join: member cells (one per union key) plus the missing-on-each-side
    /// counts, OR a <see cref="DuplicateError"/> naming a duplicate key when either side is not uniquely grouped
    /// (in which case no cells are produced — we refuse to guess or Cartesian).</summary>
    public sealed class ReconcileJoinOutcome
    {
        public List<ReconcileCell> Cells { get; } = new List<ReconcileCell>();
        public string DuplicateError { get; set; }
        public int MissingInDax { get; set; }
        public int MissingInSql { get; set; }
    }

    /// <summary>FULL OUTER JOIN of the DAX and SQL member rows by the composite key (contract a). Emits one
    /// <see cref="ReconcileCell"/> per union key — including one-sided keys (a Missing row on the absent side)
    /// and present-empty rows — and NEVER an inner join (that would delete the very one-sided keys the
    /// blank-row trap hides behind). Duplicates on either side are detected up front and refused as an
    /// InputError-shaped complaint naming the key, so a non-unique grouping can never mint Cartesian cells.</summary>
    public static class ReconcileJoiner
    {
        public static ReconcileJoinOutcome FullOuterJoin(
            IReadOnlyList<ReconcileSourceRow> dax, IReadOnlyList<ReconcileSourceRow> sql)
        {
            var outcome = new ReconcileJoinOutcome();
            var daxRows = dax ?? Array.Empty<ReconcileSourceRow>();
            var sqlRows = sql ?? Array.Empty<ReconcileSourceRow>();

            Index(daxRows, "DAX", outcome);   // DAX-side duplicate detection (map discarded; we iterate daxRows in order)
            if (outcome.DuplicateError != null) return outcome;
            var sqlByKey = Index(sqlRows, "SQL", outcome);
            if (outcome.DuplicateError != null) return outcome;

            // Union of keys, deterministic order: every DAX row in its arrival order, then the SQL-only rows in
            // theirs. A stable order keeps the worst-offender tie-breaks and the summary reproducible.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in daxRows)
            {
                if (!seen.Add(d.MatchKey)) continue;   // duplicates already refused above; guard defensively
                sqlByKey.TryGetValue(d.MatchKey, out var s);
                if (s == null) outcome.MissingInSql++;
                outcome.Cells.Add(Cell(d.DisplayKey.Length > 0 ? d.DisplayKey : s?.DisplayKey, d, s));
            }
            foreach (var s in sqlRows)
            {
                if (seen.Contains(s.MatchKey)) continue;
                if (!seen.Add(s.MatchKey)) continue;
                outcome.MissingInDax++;
                outcome.Cells.Add(Cell(s.DisplayKey, null, s));
            }
            return outcome;
        }

        private static Dictionary<string, ReconcileSourceRow> Index(
            IReadOnlyList<ReconcileSourceRow> rows, string side, ReconcileJoinOutcome outcome)
        {
            var map = new Dictionary<string, ReconcileSourceRow>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (r == null) continue;
                if (map.ContainsKey(r.MatchKey))
                {
                    outcome.DuplicateError =
                        $"Duplicate grouping key on the {side} side: [{string.Join(" | ", r.DisplayKey)}]. "
                        + "Each grouping key must occur at most once per side; a GROUP BY / SUMMARIZECOLUMNS "
                        + "that does not fully group produces duplicate keys and a Cartesian join. Fix the grouping "
                        + "(or the SQL's GROUP BY) so the composite key is unique, then re-run.";
                    return map;
                }
                map[r.MatchKey] = r;
            }
            return map;
        }

        // Map one coerced side onto a cell's per-side fields. A present row is exactly one of value / empty /
        // unsupported; an ABSENT side (null row) is a Missing row: value null, blank flag false, unsupported
        // false — the distinct state the reconciler words as "absent from the model/source".
        private static ReconcileCell Cell(string[] displayKey, ReconcileSourceRow dax, ReconcileSourceRow sql)
        {
            var cell = new ReconcileCell { GroupingKey = displayKey ?? Array.Empty<string>() };
            Apply(dax?.Value, v => cell.Dax = v, () => cell.DaxBlank = true, () => cell.DaxUnsupported = true);
            Apply(sql?.Value, v => cell.Sql = v, () => cell.SqlNull = true, () => cell.SqlUnsupported = true);
            return cell;
        }

        private static void Apply(CoercedValue? side, Action<decimal> setValue, Action setEmpty, Action setUnsupported)
        {
            if (side == null) return;                  // absent -> Missing row (leave all null/false)
            var v = side.Value;
            if (v.Unsupported) setUnsupported();
            else if (v.Empty) setEmpty();
            else if (v.Value.HasValue) setValue(v.Value.Value);
            else setEmpty();                           // defensive: a present row with no value reads as present-empty
        }
    }
}
