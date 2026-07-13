using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    // ---- DTOs for the AI-native DAX optimize/verify loop ----------------------------------------

    /// <summary>Wall-clock benchmark of a DAX query: first run vs. best/median of the warm runs.</summary>
    public sealed class BenchmarkResult
    {
        public int Runs { get; set; }
        public long FirstMs { get; set; }       // first execution (caches cold-ish for this query shape)
        public long WarmMinMs { get; set; }     // fastest warm run — the steady-state cost
        public long WarmMedianMs { get; set; }
        public long[] RunsMs { get; set; } = Array.Empty<long>();
        public int RowCount { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Cold-vs-warm benchmark stats for ONE metric (total ms OR storage-engine ms), ONE temperature.</summary>
    public sealed class ColdWarmStats
    {
        public int N { get; set; }
        public double AvgMs { get; set; }
        public double StdDevMs { get; set; }   // population standard deviation
        public long MinMs { get; set; }
        public long MaxMs { get; set; }
        public long[] RunsMs { get; set; } = Array.Empty<long>();
    }

    /// <summary>One run of a cold/warm benchmark — server-side total + SE split (when a trace is available).</summary>
    public sealed class ColdWarmRun
    {
        public int Index { get; set; }
        public bool Cold { get; set; }
        public long TotalMs { get; set; }
        public long SeMs { get; set; }
        public int SeQueries { get; set; }
    }

    /// <summary>DAX-Studio-style cold/warm benchmark: N cold runs (SE cache cleared before each) + N warm runs,
    /// with Average/StdDev/Min/Max for BOTH total query time AND storage-engine time, split by temperature.</summary>
    public sealed class ColdWarmBenchmark
    {
        public int Runs { get; set; }                  // runs per temperature
        public bool CacheClearAvailable { get; set; }  // false => the 'cold' runs aren't truly cold (cloud/refused/failed)
        public bool TraceAvailable { get; set; }       // false => totals are wall-clock; the SE split is 0
        public ColdWarmStats ColdTotal { get; set; } = new ColdWarmStats();
        public ColdWarmStats WarmTotal { get; set; } = new ColdWarmStats();
        public ColdWarmStats ColdSe { get; set; } = new ColdWarmStats();
        public ColdWarmStats WarmSe { get; set; } = new ColdWarmStats();
        public ColdWarmRun[] Detail { get; set; } = Array.Empty<ColdWarmRun>();
        public int RowCount { get; set; }
        public string Note { get; set; }
        public string Error { get; set; }
    }

    /// <summary>One row of the equivalence matrix: a context (the group-by key) where A and B were compared.</summary>
    public sealed class EquivalenceMismatch
    {
        public string Context { get; set; }     // the group-by key values for this row
        public string ValueA { get; set; }
        public string ValueB { get; set; }
    }

    /// <summary>
    /// Result of proving two DAX expressions equivalent across a filter-context matrix (each group-by
    /// row is a distinct filter context). allMatch=true means the rewrite is behavior-preserving here.
    /// </summary>
    public sealed class EquivalenceResult
    {
        public bool AllMatch { get; set; }
        public int RowsCompared { get; set; }
        public int MismatchCount { get; set; }
        public bool Truncated { get; set; }      // the comparison hit maxRows — coverage is INCOMPLETE, so a match is not a proof
        public EquivalenceMismatch[] Mismatches { get; set; } = Array.Empty<EquivalenceMismatch>();
        public string Query { get; set; }       // the comparison query that was run (for transparency)
        public string Error { get; set; }
        public bool AuthFailed { get; set; }     // Error is an XMLA/Entra AUTH failure (typed, from the ResultSet) — "sign in", not "fix the DAX"
    }

    /// <summary>
    /// The analytical optimize/verify primitives: benchmark a query's wall-clock cost, and prove a
    /// candidate rewrite returns the SAME values as the original across a filter-context matrix
    /// (built from group-by columns) before anyone applies it. ADOMD-only — no trace required.
    /// Server-side SE/FE timings are a later increment (TOM.Trace, localhost/admin only).
    /// </summary>
    public static class DaxBench
    {
        public static async Task<BenchmarkResult> BenchmarkAsync(LiveConnection lc, string query, int runs)
        {
            if (lc == null) return new BenchmarkResult { Error = "Not connected." };
            if (string.IsNullOrWhiteSpace(query)) return new BenchmarkResult { Error = "Empty query." };
            runs = Math.Max(1, Math.Min(runs <= 0 ? 5 : runs, 25));

            var times = new List<long>(runs);
            var rowCount = 0;
            for (var i = 0; i < runs; i++)
            {
                var r = await lc.ExecuteAsync(query, 1, 120); // maxRows=1: time the engine, not row marshalling
                if (!string.IsNullOrEmpty(r.Error)) return new BenchmarkResult { Error = r.Error, Runs = i };
                times.Add(r.ElapsedMs);
                rowCount = r.RowCount;
            }

            var warm = times.Skip(1).DefaultIfEmpty(times[0]).ToList();
            var sortedWarm = warm.OrderBy(t => t).ToList();
            return new BenchmarkResult
            {
                Runs = runs,
                FirstMs = times[0],
                WarmMinMs = sortedWarm.First(),
                WarmMedianMs = sortedWarm[sortedWarm.Count / 2],
                RunsMs = times.ToArray(),
                RowCount = rowCount,
                Note = "Wall-clock only (caches not force-cleared; safe-by-default). Warm = steady-state cost.",
            };
        }

        /// <summary>
        /// The DAX-Studio "Run Benchmark": measure a query COLD (clear the SE cache before each run) and WARM
        /// (no clear), and report Avg/StdDev/Min/Max for both total time and storage-engine time per temperature,
        /// plus a per-run detail list. Uses <see cref="DaxTrace.ProfileAsync"/> per run for the server-side total +
        /// SE split (so it inherits the graceful wall-clock fallback on endpoints without a trace), and
        /// <see cref="DaxCache.ClearAsync"/> for the cold runs. When <paramref name="clearForCold"/> is false (e.g.
        /// the engine-side gate degraded it on a shared endpoint), only the WARM set runs. Caller clamps the gate;
        /// this method just executes what it's told.
        /// </summary>
        public static async Task<ColdWarmBenchmark> BenchmarkColdWarmAsync(LiveConnection live, string query, int runs, bool clearForCold)
        {
            if (live == null) return new ColdWarmBenchmark { Error = "Not connected." };
            if (string.IsNullOrWhiteSpace(query)) return new ColdWarmBenchmark { Error = "Empty query." };
            runs = Math.Max(1, Math.Min(runs <= 0 ? 5 : runs, 25));

            var detail = new List<ColdWarmRun>();
            var coldTotal = new List<long>(); var coldSe = new List<long>();
            var warmTotal = new List<long>(); var warmSe = new List<long>();
            var clearAvailable = clearForCold;   // flips false if a clear fails mid-run
            var traceAvailable = false;
            var rowCount = 0;

            // COLD set first (clear before each so each run is genuinely cold); its last run leaves the cache WARM.
            if (clearForCold)
            {
                var canClear = true;   // once a clear fails, stop hammering the endpoint with doomed clears
                for (var i = 0; i < runs; i++)
                {
                    if (canClear)
                    {
                        var cc = await DaxCache.ClearAsync(live);
                        if (!cc.Cleared) { canClear = false; clearAvailable = false; }   // not truly cold (recorded, not fatal)
                    }
                    var t = await DaxTrace.ProfileAsync(live, query);
                    if (!string.IsNullOrEmpty(t.Error)) return new ColdWarmBenchmark { Error = t.Error, Runs = i };
                    traceAvailable |= t.TraceAvailable; rowCount = t.RowCount;
                    coldTotal.Add(t.TotalMs); coldSe.Add(t.SeMs);
                    detail.Add(new ColdWarmRun { Index = i + 1, Cold = true, TotalMs = t.TotalMs, SeMs = t.SeMs, SeQueries = t.SeQueries });
                }
            }

            // WARM set (no clear): the cache is warm from the cold runs (or from the first warm run if cold was skipped).
            for (var i = 0; i < runs; i++)
            {
                var t = await DaxTrace.ProfileAsync(live, query);
                if (!string.IsNullOrEmpty(t.Error)) return new ColdWarmBenchmark { Error = t.Error, Runs = runs + i };
                traceAvailable |= t.TraceAvailable; rowCount = t.RowCount;
                warmTotal.Add(t.TotalMs); warmSe.Add(t.SeMs);
                detail.Add(new ColdWarmRun { Index = i + 1, Cold = false, TotalMs = t.TotalMs, SeMs = t.SeMs, SeQueries = t.SeQueries });
            }

            return new ColdWarmBenchmark
            {
                Runs = runs,
                CacheClearAvailable = clearForCold && clearAvailable,
                TraceAvailable = traceAvailable,
                ColdTotal = Stats(coldTotal), WarmTotal = Stats(warmTotal),
                ColdSe = Stats(coldSe), WarmSe = Stats(warmSe),
                Detail = detail.ToArray(),
                RowCount = rowCount,
                Note = !clearForCold ? "Warm-only — the cache was not cleared (cold runs skipped)."
                     : !clearAvailable ? "Cache could not be cleared (needs local Power BI Desktop or an admin XMLA endpoint) — the 'cold' runs are not truly cold; treat cold ≈ warm."
                     : !traceAvailable ? "Server timings unavailable — totals are wall-clock; the SE split is 0."
                     : null,
            };
        }

        // Population mean / standard deviation / min / max over a temperature's runs.
        private static ColdWarmStats Stats(List<long> xs)
        {
            if (xs == null || xs.Count == 0) return new ColdWarmStats();
            var n = xs.Count; var avg = xs.Average();
            var variance = xs.Sum(x => (x - avg) * (x - avg)) / n;
            return new ColdWarmStats
            {
                N = n, AvgMs = Math.Round(avg, 1), StdDevMs = Math.Round(Math.Sqrt(variance), 1),
                MinMs = xs.Min(), MaxMs = xs.Max(), RunsMs = xs.ToArray(),
            };
        }

        /// <summary>
        /// Prove two scalar DAX expressions return identical values across a filter-context matrix built from
        /// <paramref name="groupBy"/>. NOT just the fully-crossed leaf rows: see <see cref="VerifyEquivalenceCoreAsync"/>
        /// — we ALSO compare at each single-column subtotal and the grand total, because a wrong-scope rewrite can
        /// match at every leaf yet diverge there. <paramref name="filters"/> are optional table filters
        /// (e.g. "KEEPFILTERS('Date'[Year] = 2024)") applied to every shape.
        /// </summary>
        public static Task<EquivalenceResult> VerifyEquivalenceAsync(
            LiveConnection lc, string exprA, string exprB, string[] groupBy, string[] filters, int maxRows)
        {
            if (lc == null) return Task.FromResult(new EquivalenceResult { Error = "Not connected." });
            // Delegate to the executor-injectable core (LiveConnection is sealed + needs a real endpoint, so the
            // multi-shape logic is only unit-testable through an injected executor). 180s matches the old timeout.
            return VerifyEquivalenceCoreAsync((q, mr) => lc.ExecuteAsync(q, mr, 180), exprA, exprB, groupBy, filters, maxRows);
        }

        /// <summary>
        /// The equivalence proof, with the query executor INJECTED (real = <see cref="LiveConnection.ExecuteAsync"/>;
        /// tests supply a fake). Why multiple queries: <c>SUMMARIZECOLUMNS(cols, …)</c> returns ONLY the fully-crossed
        /// LEAF rows — every dimension pinned in filter context — so a wrong-SCOPE rewrite (wrong denominator, a
        /// collapsed context transition, an over-broad ALL/ALLEXCEPT) can return the SAME value as the correct measure
        /// at every leaf yet DIVERGE at a subtotal or the grand total, which a leaves-only gate never tests (the
        /// ProBench "X12" wrong-but-passing class). So we compare A vs B at MULTIPLE context shapes: the grand total
        /// (no grouping), each bare single-column subtotal (each grid column ALONE), AND the fully-crossed leaves.
        /// A rewrite that diverges at ANY tested shape FAILS. Shapes are deduped (<see cref="BuildComparisonShapes"/>),
        /// so an empty grid stays the grand total only (byte-identical to the pre-fix query) and a one-column grid is
        /// that column + the grand total — no redundant re-runs.
        /// </summary>
        internal static async Task<EquivalenceResult> VerifyEquivalenceCoreAsync(
            Func<string, int, Task<ResultSet>> exec, string exprA, string exprB, string[] groupBy, string[] filters, int maxRows)
        {
            if (exec == null) return new EquivalenceResult { Error = "No query executor supplied." };
            if (string.IsNullOrWhiteSpace(exprA) || string.IsNullOrWhiteSpace(exprB))
                return new EquivalenceResult { Error = "Both exprA and exprB are required." };

            var cap = maxRows <= 0 ? 100000 : maxRows;
            // Normalize the requested grid EXACTLY as BuildComparisonShapes does, so we can pick out the LEAF shape
            // (the full requested cross) below — RowsCompared must reflect the grid's coverage, not the sum of shapes.
            var gridCols = (groupBy ?? Array.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToArray();
            var shapes = BuildComparisonShapes(groupBy);

            var mismatches = new List<EquivalenceMismatch>();
            var queries = new List<string>(shapes.Count);
            var leafRows = 0;          // rows of the GROUPED (full-grid) shape — the coverage gauge callers gate on
            var truncated = false;

            foreach (var shape in shapes)
            {
                var query = BuildComparisonQuery(exprA, exprB, shape, filters);
                queries.Add(query);
                var rs = await exec(query, cap);
                if (!string.IsNullOrEmpty(rs.Error)) return new EquivalenceResult { Query = string.Join("\n\n", queries), Error = rs.Error, AuthFailed = rs.AuthFailed };

                // Columns: [shape cols…], "__A", "__B". Find A/B by trailing position (grand total => keyCount 0).
                var n = rs.Columns.Length;
                if (n < 2) return new EquivalenceResult { Query = string.Join("\n\n", queries), Error = "Comparison query returned too few columns." };
                var aIdx = n - 2; var bIdx = n - 1;
                var keyCount = n - 2;
                // COVERAGE is measured by the requested grid (leaf) shape ONLY. The added grand-total / subtotal
                // shapes are EXTRA divergence coverage; they must NOT flip a zero-coverage grid to "proven". If the
                // grid returns no rows (e.g. an empty selection where the leaf cross has no members), a constant/ALL-
                // based measure can still yield a grand-total row — but nothing in the REQUESTED grid was compared,
                // so callers that downgrade on RowsCompared<=0 must still see 0 and stay unverified (Codex P2 guard).
                if (shape.SequenceEqual(gridCols)) leafRows = rs.RowCount;
                truncated |= rs.Truncated;

                foreach (var row in rs.Rows)
                {
                    if (!ValuesEqual(row[aIdx], row[bIdx]))
                    {
                        var ctx = keyCount > 0
                            ? string.Join(", ", Enumerable.Range(0, keyCount).Select(k => $"{rs.Columns[k].Name}={Fmt(row[k])}"))
                            : "(grand total)";
                        mismatches.Add(new EquivalenceMismatch { Context = ctx, ValueA = Fmt(row[aIdx]), ValueB = Fmt(row[bIdx]) });
                        if (mismatches.Count >= 50) break;
                    }
                }
                if (mismatches.Count >= 50) break;   // enough evidence — stop probing further shapes
            }

            return new EquivalenceResult
            {
                AllMatch = mismatches.Count == 0,
                // NOT the sum across shapes: the requested grid's row count. A populated grid + a divergence at ANY
                // shape (leaf, subtotal, or grand total) still FAILS via AllMatch; a zero-coverage grid stays 0 here.
                RowsCompared = leafRows,
                MismatchCount = mismatches.Count,
                Truncated = truncated,
                Mismatches = mismatches.ToArray(),
                Query = string.Join("\n\n", queries),
            };
        }

        // The context SHAPES the equivalence proof compares A vs B at: the grand total (no grouping) + each bare
        // single-column subtotal (each grid column ALONE) + the fully-crossed leaves (all grid columns). The leaves
        // already pin every dimension in filter context, so a wrong-scope measure diverges only at the coarser shapes
        // — which is exactly why we add them. Returned as an ORDERED, deduped set: grand total first (cheapest,
        // broadest — catches a gross wrong-scope bug early), then each single column, then the full cross. Dedup
        // collapses the degenerate sizes: an EMPTY grid yields the grand total only (identical to the pre-fix
        // behaviour) and a ONE-column grid yields {grand total, that column} — never the same SUMMARIZECOLUMNS twice.
        internal static List<string[]> BuildComparisonShapes(string[] groupBy)
        {
            var cols = (groupBy ?? Array.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToArray();
            var shapes = new List<string[]>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string[] shape)
            {
                // Order-sensitive identity; our generated shapes never reorder a caller's columns. A newline can't
                // occur in a trimmed single-line column ref, so it never collides two genuinely distinct shapes.
                if (seen.Add(string.Join("\n", shape))) shapes.Add(shape);
            }
            Add(Array.Empty<string>());                // grand total (no grouping)
            foreach (var c in cols) Add(new[] { c });  // each bare single-column subtotal
            Add(cols);                                 // the fully-crossed leaves
            return shapes;
        }

        // The actual comparison queries, one per shape, in run order (transparency + offline shape-coverage tests).
        internal static List<string> BuildComparisonQueries(string exprA, string exprB, string[] groupBy, string[] filters)
            => BuildComparisonShapes(groupBy).Select(s => BuildComparisonQuery(exprA, exprB, s, filters)).ToList();

        // ---- probe_measure: build a query that replicates a real VISUAL's filter context ----------------
        // SUMMARIZECOLUMNS(axis cols, filter args, "v", expr, "__present", 1) — axis cols = inner/visual context
        // (ISINSCOPE sees these), filter args = OUTER context (the slicers ALLSELECTED/SELECTEDVALUE restore). A
        // naive ADDCOLUMNS(VALUES(),…) has no outer filter, so context-sensitive functions resolve to the whole
        // model and report a meaningless green — placing filters as ARGS (not inside CALCULATE) reproduces the slicer.
        // The "__present",1 SENTINEL is never blank, so SUMMARIZECOLUMNS retains every axis row (it otherwise drops
        // all-blank rows) → a blank in "v" becomes observable (and distinct from a dropped row). The constant adds no
        // group-by column and no filter, so it does not perturb the filter/shadow context. Result column order is:
        // [axis cols…], v, __present — so callers locate v at index axisCols.Length (no name-matching needed).
        public static string BuildProbeQuery(string expr, string[] axisCols, string[] filterArgs)
        {
            var axis = (axisCols ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray();
            var sb = new StringBuilder();
            sb.Append("EVALUATE\nSUMMARIZECOLUMNS(\n");
            var args = new List<string>();
            foreach (var a in axis) args.Add("    " + a);
            foreach (var f in (filterArgs ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f))) args.Add("    " + f.Trim());
            args.Add("    \"v\", " + InlineScalar(expr));
            args.Add("    \"__present\", 1");
            sb.Append(string.Join(",\n", args));
            sb.Append("\n)");
            if (axis.Length > 0) sb.Append("\nORDER BY " + string.Join(", ", axis));
            return sb.ToString();
        }

        /// <summary>A single scalar evaluated under a caller-supplied filter context. A hard measure is only
        /// meaningfully proven at a slice; a grand-total probe can reject a correct time-intelligence measure or
        /// bless a wrong one. Both operands are newline-terminated so trailing line comments stay contained.</summary>
        public static string BuildScalarContextQuery(string expr, string context) =>
            "EVALUATE\nROW (\n    \"v\", CALCULATE (\n        " + InlineScalar(expr) + ",\n        "
                + (context ?? "").Trim() + "\n    )\n)";

        private const System.Globalization.NumberStyles ControlValueNumber =
            System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint;

        /// <summary>Split a control total into an optional DAX context and numeric value. The exact
        /// "context ~ value" or "measure:ref ~ context ~ value" shape opts in; every other existing probe value
        /// is returned unchanged so legacy grand-total and matrix checks cannot be silently reinterpreted.</summary>
        public static (string Context, string Value) SplitControlValue(string raw)
        {
            var s = (raw ?? "").Trim();
            var parts = s.Split('~');
            if (parts.Length == 2 || parts.Length == 3)
            {
                var ctxRaw = parts[parts.Length - 2].Trim();
                var val = parts[parts.Length - 1].Trim();
                var grandTotal = string.Equals(ctxRaw, "(grand total)", System.StringComparison.OrdinalIgnoreCase);
                var ctx = grandTotal ? "" : ctxRaw;
                if ((ctx.Length > 0 || grandTotal)
                    && double.TryParse(val, ControlValueNumber, System.Globalization.CultureInfo.InvariantCulture, out _))
                    return (ctx, val);
            }
            return ("", s);
        }

        // A slicer over a column, membership form (the honest shape TREATAS gives — set of selected members).
        // Members are DAX literals: a numeric member is emitted bare, anything else is a quoted string ("" escapes ").
        public static string CompileMemberFilter(string column, string[] members)
        {
            var lits = (members ?? Array.Empty<string>()).Where(m => m != null).Select(m =>
            {
                var t = m.Trim();
                return double.TryParse(t, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)
                    ? t : "\"" + t.Replace("\"", "\"\"") + "\"";
            }).ToArray();
            return "TREATAS ( { " + string.Join(", ", lits) + " }, " + column.Trim() + " )";
        }

        // The empty-selection context (measure should BLANK, not ERROR). TREATAS({},…) is invalid, so use FILTER(ALL,FALSE()).
        public static string CompileEmptyFilter(string column) => "FILTER ( ALL ( " + column.Trim() + " ), FALSE () )";

        /// <summary>Inline a scalar expression as a SUMMARIZECOLUMNS value argument, immune to a trailing LINE
        /// COMMENT in the expression. A stored measure body legitimately ends in "-- note"; splicing it verbatim
        /// comments out the joining comma and breaks the WHOLE query (caught live on a real model, 2026-07-03).
        /// Parenthesizing a scalar is value-preserving, and the newline before ')' terminates any line comment.</summary>
        internal static string InlineScalar(string expr) => "(\n" + (expr ?? "").Trim() + "\n    )";

        private static string BuildComparisonQuery(string exprA, string exprB, string[] groupBy, string[] filters)
        {
            var sb = new StringBuilder();
            sb.Append("EVALUATE\nSUMMARIZECOLUMNS(\n");
            var args = new List<string>();
            foreach (var g in (groupBy ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)))
                args.Add("    " + g.Trim());
            foreach (var f in (filters ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
                args.Add("    " + f.Trim());
            args.Add("    \"__A\", " + InlineScalar(exprA));
            args.Add("    \"__B\", " + InlineScalar(exprB));
            sb.Append(string.Join(",\n", args));
            sb.Append("\n)");
            return sb.ToString();
        }

        // Numeric values compared with a small relative tolerance; everything else by string form.
        // Internal: the baseline capture/compare primitive diffs values with the SAME semantics.
        internal static bool ValuesEqual(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (IsNumeric(a) && IsNumeric(b))
            {
                var da = Convert.ToDouble(a); var db = Convert.ToDouble(b);
                if (double.IsNaN(da) && double.IsNaN(db)) return true;
                var diff = Math.Abs(da - db);
                var scale = Math.Max(Math.Abs(da), Math.Abs(db));
                // Relative 1e-7 tolerates float summation-reordering noise (SUM vs SUMX, different iterators)
                // for a genuinely behavior-preserving rewrite; the 1e-9 absolute floor covers near-zero values.
                // Still far tighter than any real value change, so it never false-accepts a divergent rewrite.
                return diff <= 1e-9 || diff <= scale * 1e-7;
            }
            return string.Equals(Fmt(a), Fmt(b), StringComparison.Ordinal);
        }

        private static bool IsNumeric(object o) =>
            o is byte || o is sbyte || o is short || o is ushort || o is int || o is uint
            || o is long || o is ulong || o is float || o is double || o is decimal;

        internal static string Fmt(object o) => o switch
        {
            null => "(blank)",
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("o"),
            _ => o.ToString(),
        };
    }
}
