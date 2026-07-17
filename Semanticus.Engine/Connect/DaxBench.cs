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
        public string Fidelity { get; set; }    // non-null = the proof ran with REDUCED fidelity vs the deployed measure (inline fallback / calc-group identity) — honest caveat, not an error
        // E1 — the per-shape breakdown: one entry per EVALUATED shape (grand total, each single-axis subtotal, the
        // full cross), in run order. The shape ledger (pinned vs open) is applied by the caller on top of this.
        // Empty on ERROR results (the run stopped; Fidelity still carries the evaluation caveat).
        public ShapeComparison[] Shapes { get; set; } = Array.Empty<ShapeComparison>();
    }

    /// <summary>E1 — one evaluated equivalence shape: its canonical id ('grand_total' | 'axis:&lt;col&gt;' | 'cross'),
    /// how many rows were compared at that shape, its mismatch count, and a bounded sample of the mismatching cells.
    /// <see cref="Fidelity"/> carries the proof-level evaluation caveat (one PlanEval covers every shape), preserved
    /// per-row so a shape row read in isolation can never present a degraded comparison as full-fidelity.</summary>
    public sealed class ShapeComparison
    {
        public string ShapeId { get; set; }
        public int RowsCompared { get; set; }
        public int MismatchCount { get; set; }
        public bool Truncated { get; set; }    // THIS shape hit the row cap — kept per-shape so an OPEN shape's truncation cannot contaminate complete PINNED evidence
        public EquivalenceMismatch[] Mismatches { get; set; } = Array.Empty<EquivalenceMismatch>();
        public string Fidelity { get; set; }
    }

    /// <summary>
    /// How probe/verify/benchmark queries evaluate a candidate expression MEASURE-FAITHFULLY. Resolved by the
    /// caller against the model the query will actually RUN on (the live query connection can be attached to a
    /// DIFFERENT model than the editing session — a spec from the wrong model would silently rebind):
    /// <list type="bullet">
    /// <item><see cref="HomeTable"/> + <see cref="TargetMeasureName"/> — the target measure's REAL identity when
    /// known. Unqualified column refs bind relative to a measure's home table, and calc-group functions
    /// (ISSELECTEDMEASURE / SELECTEDMEASURENAME) observe the measure NAME — so only the real home + name reproduce
    /// deployed semantics exactly (a query-scoped DEFINE legally SHADOWS the deployed measure of the same name).</item>
    /// <item><see cref="ModelMeasureNames"/> — classifies bare [X] refs (measure vs unqualified column) and keeps
    /// generated DEFINE identifiers collision-free against real measures.</item>
    /// <item><see cref="ModelHasCalcGroups"/> — when candidates must run under GENERATED names, calc-group identity
    /// semantics are unknowable; this raises the honest fidelity flag.</item>
    /// </list>
    /// Null spec = conservative: hosts derive only from the expression's own table refs (provably present in the
    /// query text), and unclassifiable bare refs fall back to inline evaluation with a fidelity note.
    /// </summary>
    public sealed class DaxQuerySpec
    {
        public string HomeTable { get; set; }
        public string TargetMeasureName { get; set; }
        public string[] ModelMeasureNames { get; set; }
        public bool ModelHasCalcGroups { get; set; }
        /// <summary>False = the UNTRUSTED marker: an engine context existed but could not be matched to the model
        /// the query runs on (file-opened session, endpoint/database mismatch). Distinct from a null spec (no
        /// engine context at all, e.g. static callers): untrusted means the session's facts — inventory, home
        /// table, target identity, calc-group presence — are about a possibly DIFFERENT model, so PlanEval must
        /// ignore them AND note that identity-sensitive semantics are unknowable. Ambiguity always degrades;
        /// it never silently proves.</summary>
        public bool Trusted { get; set; } = true;
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
            => VerifyEquivalenceAsync(lc, exprA, exprB, groupBy, filters, maxRows, null);

        public static Task<EquivalenceResult> VerifyEquivalenceAsync(
            LiveConnection lc, string exprA, string exprB, string[] groupBy, string[] filters, int maxRows, DaxQuerySpec spec)
        {
            if (lc == null) return Task.FromResult(new EquivalenceResult { Error = "Not connected." });
            // Delegate to the executor-injectable core (LiveConnection is sealed + needs a real endpoint, so the
            // multi-shape logic is only unit-testable through an injected executor). 180s matches the old timeout.
            return VerifyEquivalenceCoreAsync((q, mr) => lc.ExecuteAsync(q, mr, 180), exprA, exprB, groupBy, filters, maxRows, spec);
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
            Func<string, int, Task<ResultSet>> exec, string exprA, string exprB, string[] groupBy, string[] filters, int maxRows, DaxQuerySpec spec = null)
        {
            if (exec == null) return new EquivalenceResult { Error = "No query executor supplied." };
            if (string.IsNullOrWhiteSpace(exprA) || string.IsNullOrWhiteSpace(exprB))
                return new EquivalenceResult { Error = "Both exprA and exprB are required." };

            // One plan for the whole proof (PlanEval is deterministic, so the per-shape builders re-derive the
            // identical DEFINE identifiers); its Note is the result's honest fidelity caveat.
            var fidelity = PlanEval(spec, comparison: true, exprA, exprB).Note;

            var cap = maxRows <= 0 ? 100000 : maxRows;
            // Normalize the requested grid EXACTLY as BuildComparisonShapes does, so we can pick out the LEAF shape
            // (the full requested cross) below — RowsCompared must reflect the grid's coverage, not the sum of shapes.
            var gridCols = NormalizeGroupBy(groupBy);
            var shapes = BuildComparisonShapes(groupBy);

            var mismatches = new List<EquivalenceMismatch>();
            var queries = new List<string>(shapes.Count);
            var shapeResults = new List<ShapeComparison>(shapes.Count);
            var leafRows = 0;          // rows of the GROUPED (full-grid) shape — the coverage gauge callers gate on
            var truncated = false;

            // E1 — every shape is evaluated (the set is tiny: 1 + ncols + 1, deduped) so a PINNED shape is never left
            // un-probed by an early global-mismatch break; per-shape samples are bounded instead.
            foreach (var shape in shapes)
            {
                var query = BuildComparisonQuery(exprA, exprB, shape, filters, spec);
                queries.Add(query);
                var rs = await exec(query, cap);
                // Error results KEEP the fidelity note: "the query failed" and "the query ran under degraded
                // evaluation" are both true, and the caller's unverified detail should say so.
                if (!string.IsNullOrEmpty(rs.Error)) return new EquivalenceResult { Query = string.Join("\n\n", queries), Error = rs.Error, AuthFailed = rs.AuthFailed, Fidelity = fidelity };

                // Columns: [shape cols…], "__A", "__B". Find A/B by trailing position (grand total => keyCount 0).
                var n = rs.Columns.Length;
                if (n < 2) return new EquivalenceResult { Query = string.Join("\n\n", queries), Error = "Comparison query returned too few columns.", Fidelity = fidelity };
                var aIdx = n - 2; var bIdx = n - 1;
                var keyCount = n - 2;
                // COVERAGE is measured by the requested grid (leaf) shape ONLY. The added grand-total / subtotal
                // shapes are EXTRA divergence coverage; they must NOT flip a zero-coverage grid to "proven". If the
                // grid returns no rows (e.g. an empty selection where the leaf cross has no members), a constant/ALL-
                // based measure can still yield a grand-total row — but nothing in the REQUESTED grid was compared,
                // so callers that downgrade on RowsCompared<=0 must still see 0 and stay unverified (Codex P2 guard).
                if (shape.SequenceEqual(gridCols)) leafRows = rs.RowCount;
                truncated |= rs.Truncated;

                var shapeSample = new List<EquivalenceMismatch>();
                var shapeMismatchCount = 0;
                foreach (var row in rs.Rows)
                {
                    if (ValuesEqual(row[aIdx], row[bIdx])) continue;
                    shapeMismatchCount++;
                    var ctx = keyCount > 0
                        ? string.Join(", ", Enumerable.Range(0, keyCount).Select(k => $"{rs.Columns[k].Name}={Fmt(row[k])}"))
                        : "(grand total)";
                    var cell = new EquivalenceMismatch { Context = ctx, ValueA = Fmt(row[aIdx]), ValueB = Fmt(row[bIdx]) };
                    if (shapeSample.Count < 10) shapeSample.Add(cell);          // bounded per-shape sample
                    if (mismatches.Count < 50) mismatches.Add(cell);           // bounded overall list
                }
                shapeResults.Add(new ShapeComparison
                {
                    ShapeId = ShapeId(shape, gridCols),
                    RowsCompared = rs.RowCount,
                    MismatchCount = shapeMismatchCount,
                    Truncated = rs.Truncated,
                    Mismatches = shapeSample.ToArray(),
                    Fidelity = fidelity,   // the proof-level caveat, preserved per row (contract: never dropped)
                });
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
                Fidelity = fidelity,
                Shapes = shapeResults.ToArray(),
            };
        }

        /// <summary>E1 — the canonical id for an evaluated shape: '' ⇒ grand_total; the full requested cross (2+ cols)
        /// ⇒ cross; a single column ⇒ axis:&lt;col&gt;. A one-column grid's only non-empty shape is the column itself,
        /// which is reported as its axis (there is no separate cross shape to name).</summary>
        internal static string ShapeId(string[] shape, string[] gridCols)
        {
            if (shape.Length == 0) return "grand_total";
            if (shape.Length >= 2 && shape.SequenceEqual(gridCols)) return "cross";
            if (shape.Length == 1) return "axis:" + shape[0];
            return "cross";
        }

        // The context SHAPES the equivalence proof compares A vs B at: the grand total (no grouping) + each bare
        // single-column subtotal (each grid column ALONE) + the fully-crossed leaves (all grid columns). The leaves
        // already pin every dimension in filter context, so a wrong-scope measure diverges only at the coarser shapes
        // — which is exactly why we add them. Returned as an ORDERED, deduped set: grand total first (cheapest,
        // broadest — catches a gross wrong-scope bug early), then each single column, then the full cross. Dedup
        // collapses the degenerate sizes: an EMPTY grid yields the grand total only (identical to the pre-fix
        // behaviour) and a ONE-column grid yields {grand total, that column} — never the same SUMMARIZECOLUMNS twice.
        /// <summary>THE group-by normalization point: trim + drop blanks. Query construction always evaluates the
        /// EFFECTIVE grid, so every consumer that reasons about matrix presence or thinness (the evidence ladder,
        /// OptInKind, oracle gating, activity labels) must use this same effective count — a raw Length would let
        /// a whitespace-only entry ([" "]) run grand-total-only yet classify as a per-context proof.</summary>
        public static string[] NormalizeGroupBy(string[] groupBy) =>
            (groupBy ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToArray();

        internal static List<string[]> BuildComparisonShapes(string[] groupBy)
        {
            var cols = NormalizeGroupBy(groupBy);
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
        internal static List<string> BuildComparisonQueries(string exprA, string exprB, string[] groupBy, string[] filters, DaxQuerySpec spec = null)
            => BuildComparisonShapes(groupBy).Select(s => BuildComparisonQuery(exprA, exprB, s, filters, spec)).ToList();

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
            => BuildProbeQuery(expr, axisCols, filterArgs, null, out _);

        public static string BuildProbeQuery(string expr, string[] axisCols, string[] filterArgs, DaxQuerySpec spec)
            => BuildProbeQuery(expr, axisCols, filterArgs, spec, out _);

        public static string BuildProbeQuery(string expr, string[] axisCols, string[] filterArgs, DaxQuerySpec spec, out string fidelityNote)
        {
            var axis = NormalizeGroupBy(axisCols);   // same normalization point as the comparison grid
            var filters = (filterArgs ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToArray();

            // MEASURE-FAITHFUL evaluation: define the candidate as a real measure and REFERENCE it, rather than
            // splicing the raw expression inline as a SUMMARIZECOLUMNS extension column. An inline scalar skips the
            // implicit CALCULATE a deployed measure carries, so a context-transition-heavy body evaluates with
            // cheaper — and sometimes DIFFERENT — semantics than production, silently mis-predicting both perf and
            // results. PlanEval owns the decision: the target's REAL home + name when the spec knows them (shadowing
            // the deployed measure preserves identity semantics exactly), an expression-derived host + generated
            // identifier otherwise, or an HONEST inline fallback (fidelityNote says why) when neither is safe.
            var plan = PlanEval(spec, comparison: false, expr);
            fidelityNote = plan.Note;
            var define = plan.HostQuoted != null;
            var value = define ? plan.Refs[0] : InlineScalar(expr);

            var sb = new StringBuilder();
            if (define) sb.Append("DEFINE\n    MEASURE ").Append(plan.HostQuoted).Append(plan.Refs[0]).Append(" = ").Append(InlineScalar(expr)).Append('\n');
            sb.Append("EVALUATE\nSUMMARIZECOLUMNS(\n");
            var args = new List<string>();
            foreach (var a in axis) args.Add("    " + a);
            foreach (var f in filters) args.Add("    " + f);
            args.Add("    \"v\", " + value);
            args.Add("    \"__present\", 1");
            sb.Append(string.Join(",\n", args));
            sb.Append("\n)");
            if (axis.Length > 0) sb.Append("\nORDER BY " + string.Join(", ", axis));
            return sb.ToString();
        }

        /// <summary>A single scalar evaluated MEASURE-FAITHFULLY (a DEFINE MEASURE via <see cref="PlanEval"/>, so the
        /// implicit CALCULATE + the target's identity are preserved) under a set of SARGable CALCULATE point filters —
        /// the expected_values anchor query. An empty filter set is the grand total. <paramref name="fidelityNote"/>
        /// carries the same reduced-fidelity caveat the equivalence/probe builders emit (null = full fidelity), so
        /// the caller can degrade an anchor proof to `unavailable` exactly as dax_equivalence does.</summary>
        public static string BuildMeasureContextQuery(string expr, string[] calculateFilters, DaxQuerySpec spec, out string fidelityNote)
        {
            var filters = (calculateFilters ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToArray();
            var plan = PlanEval(spec, comparison: false, expr);
            fidelityNote = plan.Note;
            var define = plan.HostQuoted != null;
            var value = define ? plan.Refs[0] : InlineScalar(expr);

            var sb = new StringBuilder();
            if (define) sb.Append("DEFINE\n    MEASURE ").Append(plan.HostQuoted).Append(plan.Refs[0]).Append(" = ").Append(InlineScalar(expr)).Append('\n');
            sb.Append("EVALUATE\nROW (\n    \"v\", ");
            if (filters.Length == 0) sb.Append(value);
            else sb.Append("CALCULATE (\n        ").Append(value).Append(",\n        ").Append(string.Join(",\n        ", filters)).Append("\n    )");
            sb.Append("\n)");
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

        /// <summary>Inline a scalar expression as a SUMMARIZECOLUMNS value argument (or a DEFINE MEASURE body),
        /// immune to a trailing LINE COMMENT in the expression. A stored measure body legitimately ends in "-- note";
        /// splicing it verbatim comments out the joining comma and breaks the WHOLE query (caught live on a real
        /// model, 2026-07-03). Parenthesizing a scalar is value-preserving, and the newline before ')' terminates
        /// any line comment.</summary>
        internal static string InlineScalar(string expr) => "(\n" + (expr ?? "").Trim() + "\n    )";

        // Quote a table name as a DAX identifier, doubling any embedded single quote ('O''Brien Sales').
        internal static string QuoteTable(string name) => "'" + (name ?? "").Replace("'", "''") + "'";

        // Bracket a measure name as a DAX reference, doubling any embedded ']' ([Weird ]] Name]).
        internal static string BracketName(string name) => "[" + (name ?? "").Replace("]", "]]") + "]";

        // ---- token-aware DAX reference scanning (host derivation + bare-ref classification) ----------------
        // A regex over raw DAX misparses escaped quotes ('O''Brien Sales'[X] → "Brien Sales") and matches refs
        // inside comments and string literals. So: ONE state-machine pass strips // -- /* */ comments and
        // "double-quoted" strings (both replaced by a space so tokens can't fuse), preserving BOTH identifier
        // forms — 'quoted table identifiers' AND [bracketed identifiers] (a name like [Gross -- Net] or
        // [Caption "Q1"] legally contains comment/string markers, so brackets must be lexed as identifiers here,
        // not scanned for markers). Nested /* */ is not depth-tracked — DAX does not nest block comments.
        internal static string StripCommentsAndStrings(string dax)
        {
            if (string.IsNullOrEmpty(dax)) return dax ?? "";
            var sb = new StringBuilder(dax.Length);
            int i = 0, n = dax.Length;
            while (i < n)
            {
                char c = dax[i];
                if (c == '/' && i + 1 < n && dax[i + 1] == '/') { while (i < n && dax[i] != '\n') i++; sb.Append(' '); continue; }
                if (c == '-' && i + 1 < n && dax[i + 1] == '-') { while (i < n && dax[i] != '\n') i++; sb.Append(' '); continue; }
                if (c == '/' && i + 1 < n && dax[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < n && !(dax[i] == '*' && dax[i + 1] == '/')) i++;
                    i = Math.Min(n, i + 2); sb.Append(' '); continue;
                }
                if (c == '"')   // string literal, "" = escaped quote; replaced wholesale
                {
                    i++;
                    while (i < n) { if (dax[i] == '"') { if (i + 1 < n && dax[i + 1] == '"') { i += 2; continue; } i++; break; } i++; }
                    sb.Append(' '); continue;
                }
                if (c == '\'')  // quoted table identifier, '' = escaped quote; KEPT verbatim for the ref scanner
                {
                    sb.Append(c); i++;
                    while (i < n)
                    {
                        if (dax[i] == '\'') { if (i + 1 < n && dax[i + 1] == '\'') { sb.Append("''"); i += 2; continue; } sb.Append('\''); i++; break; }
                        sb.Append(dax[i]); i++;
                    }
                    continue;
                }
                if (c == '[')   // bracketed identifier, ]] = escaped bracket; KEPT verbatim (markers inside are NAME text)
                {
                    sb.Append(c); i++;
                    while (i < n)
                    {
                        if (dax[i] == ']') { if (i + 1 < n && dax[i + 1] == ']') { sb.Append("]]"); i += 2; continue; } sb.Append(']'); i++; break; }
                        sb.Append(dax[i]); i++;
                    }
                    continue;
                }
                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        /// <summary>Scan SANITIZED DAX (see <see cref="StripCommentsAndStrings"/>) for table-qualified refs
        /// ('Table'[Col] / Table[Col] → table name, '' unescaped) and BARE bracket refs ([X] with no qualifier —
        /// a measure reference or an unqualified column reference; syntactically indistinguishable).
        /// <paramref name="qualifiedRefs"/> (optional) receives the full (table, name) pair of each qualified ref
        /// — both parts unescaped — for callers that need the member name too (the circular-rewrite guard).</summary>
        internal static void ScanRefs(string sanitized, List<string> qualifiedTables, List<string> bareRefs,
            List<(string Table, string Name)> qualifiedRefs = null)
        {
            int i = 0, n = sanitized?.Length ?? 0;
            // read a [bracket ref] starting at index of '['; returns the unescaped inner name, advances past ']'
            string ReadBracket(ref int p)
            {
                var name = new StringBuilder(); p++;   // past '['
                while (p < n)
                {
                    if (sanitized[p] == ']')
                    {
                        if (p + 1 < n && sanitized[p + 1] == ']') { name.Append(']'); p += 2; continue; }
                        p++; break;
                    }
                    name.Append(sanitized[p]); p++;
                }
                return name.ToString();
            }
            while (i < n)
            {
                char c = sanitized[i];
                if (c == '\'')                         // quoted table identifier
                {
                    var name = new StringBuilder(); i++;
                    while (i < n)
                    {
                        if (sanitized[i] == '\'') { if (i + 1 < n && sanitized[i + 1] == '\'') { name.Append('\''); i += 2; continue; } i++; break; }
                        name.Append(sanitized[i]); i++;
                    }
                    int j = i; while (j < n && char.IsWhiteSpace(sanitized[j])) j++;
                    if (j < n && sanitized[j] == '[' && name.Length > 0)
                    {
                        qualifiedTables?.Add(name.ToString());
                        i = j; var member = ReadBracket(ref i);
                        qualifiedRefs?.Add((name.ToString(), member));
                    }
                    continue;
                }
                if (char.IsLetter(c) || c == '_')      // bareword: identifier — a table ref iff followed by '['
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(sanitized[i]) || sanitized[i] == '_')) i++;
                    int j = i; while (j < n && char.IsWhiteSpace(sanitized[j])) j++;
                    if (j < n && sanitized[j] == '[')
                    {
                        var tbl = sanitized.Substring(start, i - start);
                        qualifiedTables?.Add(tbl);
                        i = j; var member = ReadBracket(ref i);
                        qualifiedRefs?.Add((tbl, member));
                    }
                    continue;
                }
                if (c == '[') { var b = ReadBracket(ref i); if (b.Length > 0) bareRefs?.Add(b); continue; }
                i++;
            }
        }

        /// <summary>A candidate references the target measure ITSELF — a circular rewrite. Validated BEFORE any
        /// comparison/apply: shipping it would only fail later inside the engine with a generic error (or worse,
        /// mutate the model into a self-referencing measure). Two circular shapes, both token-aware (comments and
        /// strings don't count; '' and ]] escapes are unescaped by the scanner; names compare case-insensitively):
        /// a BARE ref to the target's name ([M]), and a QUALIFIED ref whose TABLE is the target's HOME table AND
        /// whose member is the target's name ('Home'[M] — equally circular; DAX resolves it to the measure).
        /// A same-named qualified ref on a DIFFERENT table is NOT circular: measure names are model-unique, so a
        /// table mismatch means the ref is a column. Ambiguity stays conservative — name equals the target's AND
        /// table equals its home ⇒ reject, even if a same-named column also exists there.</summary>
        internal static bool ReferencesTarget(string expr, string targetName, string homeTable = null)
        {
            if (string.IsNullOrWhiteSpace(expr) || string.IsNullOrWhiteSpace(targetName)) return false;
            var bare = new List<string>();
            var qrefs = new List<(string Table, string Name)>();
            ScanRefs(StripCommentsAndStrings(expr), null, bare, qrefs);
            if (bare.Any(b => string.Equals(b, targetName, StringComparison.OrdinalIgnoreCase))) return true;
            return !string.IsNullOrWhiteSpace(homeTable) && qrefs.Any(q =>
                string.Equals(q.Table, homeTable.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(q.Name, targetName, StringComparison.OrdinalIgnoreCase));
        }

        internal const string CircularRewriteNote = "candidate references the target measure itself — circular rewrite";

        // The ENTIRE expression is one bare bracket ref ("[Total Sales]" — the Baseline.MeasureRefExpr shape).
        // Already measure-faithful inline: the reference itself carries the implicit CALCULATE AND preserves the
        // deployed measure's calc-group identity (a DEFINE wrapper would rename what SELECTEDMEASURENAME sees).
        internal static bool IsBareMeasureRef(string expr)
        {
            var t = (expr ?? "").Trim();
            if (t.Length < 3 || t[0] != '[') return false;
            for (var p = 1; p < t.Length; p++)
            {
                if (t[p] != ']') continue;
                if (p + 1 < t.Length && t[p + 1] == ']') { p++; continue; }   // ]] = escaped, keep scanning
                return p == t.Length - 1;                                     // the ref must close AT the end
            }
            return false;
        }

        /// <summary>The resolved evaluation plan both query builders consume — ONE decision point for
        /// measure-faithfulness (sol's query-spec centralization).</summary>
        internal sealed class EvalPlan
        {
            public string HostQuoted;    // ready-to-splice 'Table' ref; null => inline evaluation
            public string[] Refs;        // bracketed DEFINE identifiers to reference, one per candidate
            public string Note;          // fidelity caveat for the result payload (null = full fidelity)
        }

        // First 8 hex chars of SHA-256 — deterministic per candidate set, so the same verify re-issues the same
        // query text (FE-cache behavior and transparency logs stay stable) while staying collision-checkable.
        private static string Hash8(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
            return Convert.ToHexString(h, 0, 4).ToLowerInvariant();
        }

        private const string CalcGroupNote =
            "The model contains calculation groups: the candidates run under query-scoped names, so "
            + "identity-sensitive functions (ISSELECTEDMEASURE / SELECTEDMEASURENAME) do not see the deployed "
            + "measure's name. A calc item keyed on measure identity may behave differently than in production.";

        private static string UnclassifiableNote(string bareRef) =>
            "Evaluated INLINE, not as a deployed measure: the expression contains a bare reference ("
            + BracketName(bareRef) + ") that could be an unqualified column reference, and no target measure "
            + "identity was supplied — a DEFINE MEASURE home table cannot be chosen safely (unqualified columns "
            + "bind to the measure's home table). Context-transition and calculation-group semantics may differ "
            + "from the deployed measure.";

        /// <summary>
        /// Decide how candidate expressions evaluate. Rules, in order (TARGET IDENTITY FIRST):
        /// 1. Known target identity (real home + name) → DEFINE on the REAL home table. A single candidate SHADOWS
        ///    the deployed measure's real name (a query-scoped DEFINE legally overrides it, so ISSELECTEDMEASURE /
        ///    SELECTEDMEASURENAME see the true identity) — even a bare-ref candidate, because as the target's body
        ///    it must run AS the target, not under the referenced measure's own identity. A candidate that IS the
        ///    target ref ([M] as a rewrite of M) becomes a circular definition the engine rejects LOUDLY — the
        ///    correct verdict on a circular rewrite. Comparisons need TWO bodies for one identity — unknowable —
        ///    so both get generated names, plus the calc-group note when groups exist.
        /// 2. No target, every candidate a single bare ref that CLASSIFIES as a model measure → INLINE, note-free
        ///    (a measure reference is identity-exact for itself). Unclassifiable → still inline (no other host is
        ///    safe) but WITH the fidelity note.
        /// 3. Raw candidates: every bare ref must classify as a measure (unqualified COLUMN refs bind relative to
        ///    a measure's HOME table, which we don't know) — else INLINE + note (honesty over silent wrongness).
        /// 4. Host: the first table ref scanned from the candidates' own DAX (provably exists on the model the
        ///    query runs on — a session-derived guess could silently rebind), else spec.HomeTable. No host at all
        ///    (constants / measure-only composites) → inline, noted when calc groups exist or measures are
        ///    referenced (application-point semantics differ from a deployed measure).
        /// 5. Names: generated collision-free __smx_&lt;hash8&gt;_probe/_A/_B, checked against the measure inventory
        ///    AND the candidate text; calc groups in the model → the honest identity note.
        /// </summary>
        internal static EvalPlan PlanEval(DaxQuerySpec spec, bool comparison, params string[] exprs)
        {
            var suffixes = comparison ? new[] { "A", "B" } : new[] { "probe" };
            // An UNTRUSTED spec's facts describe a possibly different model: ignore its identity/inventory/home
            // (using them could silently rebind), and remember that calc-group presence is UNKNOWN — every
            // identity-sensitive path below must then degrade with a note rather than stay silent.
            var untrusted = spec != null && !spec.Trusted;
            var targetKnown = !untrusted && spec != null && !string.IsNullOrWhiteSpace(spec.TargetMeasureName) && !string.IsNullOrWhiteSpace(spec.HomeTable);
            var inventory = untrusted || spec?.ModelMeasureNames == null ? null
                : new HashSet<string>(spec.ModelMeasureNames, StringComparer.OrdinalIgnoreCase);
            // True = groups exist; also degrade when presence is UNKNOWABLE (untrusted spec).
            var calcGroupRisk = untrusted || (spec?.ModelHasCalcGroups ?? false);
            var calcGroupNote = untrusted
                ? "The live connection could not be matched to the editing session (file-opened session or a different "
                  + "endpoint/database), so calculation-group presence and measure identity on the CONNECTED model are "
                  + "unknown — the candidates run under query-scoped names whose identity-sensitive semantics "
                  + "(ISSELECTEDMEASURE / SELECTEDMEASURENAME) may differ from a deployed measure. Open the model with "
                  + "open_live/open_local for a full-fidelity proof."
                : CalcGroupNote;

            var tables = new List<string>();
            var bare = new List<string>();
            foreach (var e in exprs) ScanRefs(StripCommentsAndStrings(e), tables, bare);

            string[] GenerateIds()
            {
                var seed = string.Join("", exprs);
                for (var k = 0; ; k++)
                {
                    var nonce = Hash8(k == 0 ? seed : seed + "#" + k);
                    var ids = suffixes.Select(sfx => "__smx_" + nonce + "_" + sfx).ToArray();
                    var clash = ids.Any(id => (inventory != null && inventory.Contains(id))
                                              || exprs.Any(e => e != null && e.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0));
                    if (!clash) return ids.Select(id => "[" + id + "]").ToArray();
                }
            }

            // Rule 1: target identity dominates every fast path (a bare-ref candidate still runs AS the target).
            if (targetKnown)
            {
                var shadowPlan = new EvalPlan { HostQuoted = QuoteTable(spec.HomeTable.Trim()) };
                if (!comparison) { shadowPlan.Refs = new[] { BracketName(spec.TargetMeasureName) }; return shadowPlan; }
                shadowPlan.Refs = GenerateIds();
                if (calcGroupRisk) shadowPlan.Note = calcGroupNote;
                return shadowPlan;
            }

            // Rule 2: pure measure refs — inline; note-free ONLY when each classifies as a real model measure.
            if (exprs.All(IsBareMeasureRef))
            {
                var unknown = bare.FirstOrDefault(b => inventory == null || !inventory.Contains(b));
                return unknown == null ? new EvalPlan() : new EvalPlan { Note = UnclassifiableNote(unknown) };
            }

            // Rule 3: raw candidates with unclassifiable bare refs → inline + note.
            var suspect = bare.FirstOrDefault(b => inventory == null || !inventory.Contains(b));
            if (suspect != null) return new EvalPlan { Note = UnclassifiableNote(suspect) };

            // Rule 4: host — expression-derived first (provably exists where the query runs), spec fallback second
            // (never an UNTRUSTED spec's table: it may not exist on the connected model).
            var host = tables.Count > 0 ? tables[0] : (untrusted ? null : spec?.HomeTable);
            if (string.IsNullOrWhiteSpace(host))
                return new EvalPlan
                {
                    // Constants stay silent; measure-referencing composites / calc-group(-unknown) models get the
                    // honest note (inline changes WHERE calc items and identity apply vs a deployed measure).
                    Note = (calcGroupRisk || bare.Count > 0)
                        ? "Evaluated INLINE: no DEFINE MEASURE host table is derivable (no table-qualified column in the "
                          + "expression and none supplied), so calculation-group application and measure-identity semantics "
                          + "may differ from a deployed measure."
                        : null,
                };

            // Rule 5: generated identifiers + the calc-group identity caveat (known groups OR unknowable presence).
            var plan = new EvalPlan { HostQuoted = QuoteTable(host.Trim()), Refs = GenerateIds() };
            if (spec != null && calcGroupRisk) plan.Note = calcGroupNote;
            return plan;
        }

        /// <summary>
        /// The SHARED Verified-Edits evidence ladder (optimize_measure, apply_plan set_dax verify, the MCP
        /// activity label, interview paraphrase scoring): grade one equivalence result into a state + reason.
        /// Order IS the contract — run-failure, DEGRADED MISMATCH, value mismatch, zero coverage, truncation,
        /// DEGRADED FIDELITY, thin grid, proven. Fidelity sits BEFORE mismatch on purpose: under a degraded
        /// comparison the surrogate itself can cause the divergence (calc-group identity on generated names), so
        /// a mismatch there is an OBSERVATION, not a conviction — it still blocks like failed, but must never be
        /// reported as a proven behavior change. Fidelity also sits above the thin-grid rung: a degraded match
        /// must never ride apply_plan's apply-with-label path.
        /// States: unverified | degraded_mismatch | failed | degraded | thin | proven.
        /// </summary>
        internal static (string State, string Note) ClassifyEquivalenceEvidence(EquivalenceResult v, int groupByLength)
        {
            if (v == null) return ("unverified", "equivalence check returned nothing");
            if (!string.IsNullOrEmpty(v.Error))
                // Error still ranks FIRST (nothing ran to completion), but a degraded-evaluation caveat survives
                // into the detail — the failure happened under the surrogate, which may itself be the cause.
                return ("unverified", "equivalence check failed to run — " + v.Error
                    + (string.IsNullOrEmpty(v.Fidelity) ? "" : " (ran under degraded evaluation: " + v.Fidelity + ")"));
            if (!v.AllMatch && !string.IsNullOrEmpty(v.Fidelity))
                return ("degraded_mismatch", $"difference observed in {v.MismatchCount} context(s) under a DEGRADED comparison — not authoritative (the reduced-fidelity surrogate itself can cause divergence): {v.Fidelity}");
            if (!v.AllMatch) return ("failed", $"changes results in {v.MismatchCount} context(s)");
            if (v.RowsCompared <= 0) return ("unverified", "equivalence check compared 0 rows (nothing to prove)");
            if (v.Truncated) return ("unverified", $"equivalence matrix exceeded the row cap ({v.RowsCompared}+ rows) — coverage incomplete");
            if (!string.IsNullOrEmpty(v.Fidelity)) return ("degraded", "values matched, but the comparison ran with REDUCED fidelity — " + v.Fidelity);
            if (groupByLength == 0) return ("thin", "grand-total match only — not a per-context equivalence proof");
            return ("proven", null);
        }

        // internal (not private) so offline shape tests can pin ONE shape's exact measure-faithful text.
        internal static string BuildComparisonQuery(string exprA, string exprB, string[] groupBy, string[] filters, DaxQuerySpec spec = null)
        {
            var grid = NormalizeGroupBy(groupBy);   // THE normalization point — never a re-derivation
            var flt = (filters ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToArray();

            // Measure-faithful AND same-query: define BOTH candidates as measures in ONE DEFINE block so they see an
            // IDENTICAL filter context, then compare their referenced values. Inline extension columns would drop the
            // implicit CALCULATE and could make A and B agree (or disagree) for the wrong reason — the fidelity gap
            // this equivalence gate exists to close. PlanEval owns the host/name/fallback decisions; the OUTPUT
            // column aliases stay "__A"/"__B" whatever the DEFINE identifiers are (callers parse those aliases).
            var plan = PlanEval(spec, comparison: true, exprA, exprB);
            var define = plan.HostQuoted != null;
            var valA = define ? plan.Refs[0] : InlineScalar(exprA);
            var valB = define ? plan.Refs[1] : InlineScalar(exprB);

            var sb = new StringBuilder();
            if (define)
                sb.Append("DEFINE\n    MEASURE ").Append(plan.HostQuoted).Append(plan.Refs[0]).Append(" = ").Append(InlineScalar(exprA)).Append('\n')
                  .Append("    MEASURE ").Append(plan.HostQuoted).Append(plan.Refs[1]).Append(" = ").Append(InlineScalar(exprB)).Append('\n');
            sb.Append("EVALUATE\nSUMMARIZECOLUMNS(\n");
            var args = new List<string>();
            foreach (var g in grid) args.Add("    " + g);
            foreach (var f in flt) args.Add("    " + f);
            args.Add("    \"__A\", " + valA);
            args.Add("    \"__B\", " + valB);
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
